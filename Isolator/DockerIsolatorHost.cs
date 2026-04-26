using Docker.DotNet;
using Docker.DotNet.BasicAuth;
using Docker.DotNet.Models;
using System.Runtime.InteropServices;
using System.Text;

namespace Isolator;

public class DockerIsolatorHost : IIsolationHost
{
    private DockerClient? _client;

    public DockerIsolatorHost()
    {
        _client = new DockerClientConfiguration().CreateClient();
    }

    public DockerIsolatorHost(Uri dockerUrl, string? username = null, string? password = null)
    {
        Credentials credentials = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password) ?
            new BasicAuthCredentials(username!, password!) :
            new AnonymousCredentials();

        _client = new DockerClientConfiguration(dockerUrl, credentials).CreateClient();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }

    public System.Version NetVersion { get; init; } = new System.Version(9, 0);

    private async Task<PluginExecutionResult> RunAsync(
            string hostFolder,          // e.g. /home/me/app/publish  (must be absolute)
            string assemblyFileName,    // e.g. MyApp
            string pluginTypeName,      // e.g. MyApp.MyPlugin, MyApp, Version=
            IsolationContext context,
            CancellationToken cancellationToken = default)
    {
        var contextJson = IsolationHelper.Serialize(context);

        // For Linux Docker engine: unix socket. For Windows (named pipe): npipe://./pipe/docker_engine
        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";

        var image = $"mcr.microsoft.com/dotnet/runtime:{NetVersion}";

        // Ensure image exists locally (pull if needed)
        await _client!.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image },
            authConfig: null,
            progress: new Progress<JSONMessage>(),
            cancellationToken: cancellationToken);

        var containerMountPath = "/work";
        var containerAssemblyPath = $"{containerMountPath}/Isolator.Docker.exe";

        // Create container
        var createResponse = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = image,
            WorkingDir = containerMountPath,
            Name = $"isolator-{pluginTypeName.Split(',')[0]}-{Guid.NewGuid():N}",
            Cmd = ["dotnet", containerAssemblyPath, pluginTypeName, contextJson],
            HostConfig = new HostConfig
            {
                Binds =
                [
                    $"{hostFolder}:{containerMountPath}:ro"
                ],
                AutoRemove = true,
            },
        }, cancellationToken);

        var containerId = createResponse.ID;

        // Attach to logs (stdout and stderr separately)
        using var logstdoutStream = await _client.Containers.AttachContainerAsync(
            containerId,
            tty: false,
            new ContainerAttachParameters { Stream = true, Stdout = true, Stderr = false },
            cancellationToken);

        using var logstderrStream = await _client.Containers.AttachContainerAsync(
            containerId,
            tty: false,
            new ContainerAttachParameters { Stream = true, Stdout = false, Stderr = true },
            cancellationToken);

        // Start container
        if (!await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken))
        {
            throw new Exception("Failed to start container.");
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        // Read logs concurrently
        var logstdoutTask = Task.Run(async () =>
        {
            var buffer = new byte[8192];

            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await logstdoutStream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);

                if (result.EOF)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);

                stdout.Append(text);
            }
        }, cancellationToken);

        var logstderrTask = Task.Run(async () =>
        {
            var buffer = new byte[8192];

            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await logstderrStream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);

                if (result.EOF)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);

                stderr.Append(text);
            }
        }, cancellationToken);

        // Wait for exit
        var wait = await _client.Containers.WaitContainerAsync(containerId, cancellationToken);

        await logstdoutTask;
        await logstderrTask;

        if (stdout.Length == 0 && stderr.Length > 0)
        {
            throw new Exception(stderr.ToString());
        }

        var result = IsolationHelper.Deserialize<ExecutionResult>(stdout.ToString());
        object? resultObject = null;

        if (result?.Result != null && !string.IsNullOrWhiteSpace(result?.ResultType))
        {
            resultObject = IsolationHelper.Deserialize(result.Result, Type.GetType(result.ResultType)!);
        }

        CopyProperties(result!.Properties, context.Properties);

        var pluginResult = new PluginExecutionResult(result!.StandardOutput, result.StandardError, resultObject);

        return pluginResult;
    }

    private static void CopyProperties(Dictionary<string, object> sourceProperties, Dictionary<string, object> targetProperties)
    {
        foreach (var kv in sourceProperties)
        {
            targetProperties[kv.Key] = kv.Value;
        }
    }

    public async Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default) where TPlugin : IPlugin, new()
    {
        ObjectDisposedException.ThrowIf(_client is null, nameof(DockerIsolatorHost));

        var hostFolder = Path.GetDirectoryName(plugin.GetType().Assembly.Location);
        var assemblyFileName = Path.GetFileName(plugin.GetType().Assembly.Location);

        var result = await RunAsync(hostFolder!, assemblyFileName, plugin.GetType().AssemblyQualifiedName!, context, cancellationToken);

        return result;
    }
}

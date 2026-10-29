using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;

namespace Isolator;

public static class MxcIsolationProperties
{
    public const string Filesystem = "Mxc.Filesystem";
    public const string Network = "Mxc.Network";
    public const string Ui = "Mxc.Ui";
    public const string Containment = "Mxc.Containment";
    public const string TimeoutMs = "Mxc.TimeoutMs";
    public const string ContainerName = "Mxc.ContainerName";
    public const string WorkingDirectory = "Mxc.WorkingDirectory";
    public const string Environment = "Mxc.Environment";
    public const string InheritDefaultEnvironment = "Mxc.InheritDefaultEnvironment";
}

public sealed class MxcIsolationHost : BaseIsolationHost
{
    private static readonly string _programSource = $$"""
        using System;
        using System.IO;
        using Isolator;
        using System.Reflection;

        internal class Program
        {
            public static void Main()
            {
                var args = Environment.GetCommandLineArgs();
                var envelope = IsolationHelper.Deserialize<PluginEnvelope>(File.ReadAllText(args[1]));
                _ = Assembly.LoadFrom(envelope.PluginAssemblyPath);
                var pluginType = Type.GetType(envelope.PluginType, throwOnError: true)!;
                var plugin = IsolationHelper.Deserialize(envelope.PluginJson, pluginType) as IPlugin
                    ?? throw new InvalidOperationException("Failed to deserialize plugin instance or not a plugin type.");
                var context = IsolationHelper.Deserialize<IsolationContext>(envelope.ContextJson);

                ExecutionResult response;
                using (var stdout = IsolationHelper.CaptureOutput())
                using (var stderr = IsolationHelper.CaptureError())
                {
                    var result = plugin.Execute(context);
                    response = new(
                        StandardOutput: stdout.ToString(),
                        StandardError: stderr.ToString(),
                        Result: result,
                        ResultType: result?.GetType()?.AssemblyQualifiedName,
                        Properties: context.Properties);
                }

                Console.Out.WriteLine(IsolationHelper.Serialize(response));
            }
        }
        """;

    public override async Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(
        TPlugin plugin,
        IsolationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var pluginAssemblyPath = plugin.GetType().Assembly.Location;
        if (string.IsNullOrWhiteSpace(pluginAssemblyPath) || !File.Exists(pluginAssemblyPath))
        {
            throw new InvalidOperationException("The plugin assembly must have a file location.");
        }

        var tempDir = CreateTempDirectory();
        try
        {
            var (outputDir, dllPath) = await CompileRunnerAsync(
                _programSource,
                tempDir.FullName,
                isApp: true,
                cancellationToken);

            var version = new Version(Environment.Version.Major, Environment.Version.Minor, 0);
            var runtimeConfig = $$"""
                {
                  "runtimeOptions": {
                    "tfm": "net9.0",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "{{version}}"
                    },
                    "rollForward": "LatestMinor"
                  }
                }
                """;
            await File.WriteAllTextAsync(
                Path.Combine(outputDir, $"{_runnerName}.runtimeconfig.json"),
                runtimeConfig,
                cancellationToken);

            CopyIsolationAssembly(outputDir);

            var envelopePath = Path.Combine(outputDir, "Plugin.json");
            var envelope = new PluginEnvelope(
                PluginType: plugin.GetType().AssemblyQualifiedName!,
                PluginJson: IsolationHelper.Serialize(plugin),
                PluginAssemblyPath: pluginAssemblyPath,
                ContextJson: IsolationHelper.Serialize(context));
            await File.WriteAllTextAsync(
                envelopePath,
                IsolationHelper.Serialize(envelope),
                cancellationToken);

            var filesystem = GetProperty<FilesystemPolicy>(context, MxcIsolationProperties.Filesystem)
                ?? new FilesystemPolicy();
            filesystem = CopyFilesystemPolicy(filesystem);
            AddReadOnlyPath(filesystem, outputDir);
            var pluginDirectory = Path.GetDirectoryName(pluginAssemblyPath);
            if (pluginDirectory is not null)
            {
                AddReadOnlyPath(filesystem, pluginDirectory);
            }

            var request = new ContainerRequest(
                $"{QuoteCommandArgument("dotnet")} {QuoteCommandArgument(dllPath)} {QuoteCommandArgument(envelopePath)}")
            {
                Containment = GetProperty<Containment>(context, MxcIsolationProperties.Containment)
                    ?? new Containment.Process(),
                Filesystem = filesystem,
                Network = GetProperty<NetworkPolicy>(context, MxcIsolationProperties.Network),
                Ui = GetProperty<UiPolicy>(context, MxcIsolationProperties.Ui),
                TimeoutMs = GetProperty<uint?>(context, MxcIsolationProperties.TimeoutMs),
                ContainerName = GetProperty<string>(context, MxcIsolationProperties.ContainerName),
                WorkingDirectory = GetProperty<string>(context, MxcIsolationProperties.WorkingDirectory)
                    ?? outputDir,
                Environment = GetProperty<Dictionary<string, string>>(context, MxcIsolationProperties.Environment),
                InheritDefaultEnvironment = GetProperty<bool>(context, MxcIsolationProperties.InheritDefaultEnvironment),
            };

            var runResult = await MxcContainer.RunAsync(request, cancellationToken: cancellationToken);
            if (runResult.TimedOut)
            {
                throw new TimeoutException("MXC terminated the plugin after its execution timeout.");
            }
            if (runResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"MXC plugin process exited with code {runResult.ExitCode}: {runResult.Stderr}");
            }

            var execution = IsolationHelper.Deserialize<ExecutionResult>(runResult.Stdout);
            CopyProperties(execution.Properties, context.Properties);

            var result = execution.Result;
            if (result is not null && !string.IsNullOrWhiteSpace(execution.ResultType))
            {
                result = IsolationHelper.Deserialize(result, Type.GetType(execution.ResultType)!);
            }

            return new PluginExecutionResult(execution.StandardOutput, execution.StandardError, result);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private static T? GetProperty<T>(IsolationContext context, string name)
    {
        if (!context.Properties.TryGetValue(name, out var value) || value is null)
        {
            return default;
        }

        if (value is T typedValue)
        {
            return typedValue;
        }

        if (value is JsonElement element)
        {
            return element.Deserialize<T>();
        }

        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
    }

    private static FilesystemPolicy CopyFilesystemPolicy(FilesystemPolicy filesystem) =>
        new()
        {
            ReadwritePaths = [.. filesystem.ReadwritePaths],
            ReadonlyPaths = [.. filesystem.ReadonlyPaths],
            DeniedPaths = [.. filesystem.DeniedPaths],
            ClearPolicyOnExit = filesystem.ClearPolicyOnExit,
        };

    private static void AddReadOnlyPath(FilesystemPolicy filesystem, string path)
    {
        if (!filesystem.ReadonlyPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            filesystem.ReadonlyPaths.Add(path);
        }
    }

    private static string QuoteCommandArgument(string argument) =>
        OperatingSystem.IsWindows()
            ? $"\"{argument}\""
            : $"'{argument.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}

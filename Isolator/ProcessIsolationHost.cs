using System.Diagnostics;
using System.Text;

namespace Isolator;

public sealed class ProcessIsolationHost : BaseIsolationHost
{
    private const string _dotnetFileName = "dotnet";
    private static readonly string _version = new Version(Environment.Version.Major, Environment.Version.Minor, 0).ToString();
    private static readonly string _runtimeConfig = $"{{\n \"runtimeOptions\": {{\n \"tfm\": \"net9.0\",\n \"framework\": {{\n \"name\": \"Microsoft.NETCore.App\",\n \"version\": \"{_version}\"\n }},\n \"rollForward\": \"LatestMinor\"\n }}\n}}";

    private static readonly string _programSource = $$"""
        using Isolator;
        [assembly:System.CodeDom.Compiler.GeneratedCode("{{typeof(IsolationHelper).Namespace}}", "{{typeof(IsolationHelper).Assembly.GetName().Version?.ToString()}}")]
        internal class Program
        {
            public static void Main()
            {
                var (plugin, ctx) = {{nameof(IsolationHelper)}}.{{nameof(IsolationHelper.GetProcessBootstrap)}}();
                {{nameof(ExecutionResult)}} response;
                using (var stdout = {{nameof(IsolationHelper)}}.{{nameof(IsolationHelper.CaptureOutput)}}())
                using (var stderr = {{nameof(IsolationHelper)}}.{{nameof(IsolationHelper.CaptureError)}}())
                {
                    var result = plugin.{{nameof(IPlugin.Execute)}}(ctx);
                    response = new(
                        Result: result,
                        ResultType: result?.GetType()?.FullName,
                        StandardOutput: stdout.ToString(),
                        StandardError: stderr.ToString(),
                        Properties: ctx.Properties
                    );
                }

                System.Console.Out.WriteLine({{nameof(IsolationHelper)}}.{{nameof(IsolationHelper.Serialize)}}(response));
            }
        }
        """;
    private readonly string _userName = string.Empty;
    private readonly string? _password = null;
    private readonly string _domain = string.Empty;
    private readonly bool _loadUserProfile = false;

    public ProcessIsolationHost()
    {        
    }

    public ProcessIsolationHost(string userName, string password, string domain, bool loadUserProfile)
    {
        _userName = userName;
        _password = password;
        _domain = domain;
        _loadUserProfile = loadUserProfile;
    }

    public override async Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);

        var tempDir = CreateTempDirectory();
        try
        {
            // Compile the minimal runner in-memory and emit artifacts to the temp directory
            var (outputDir, dllPath) = await CompileRunnerAsync(_programSource, tempDir.FullName, true, cancellationToken);

            // Write minimal runtimeconfig.json so 'dotnet Runner.dll' can launch
            await File.WriteAllTextAsync(Path.Combine(outputDir, $"{_runnerName}.runtimeconfig.json"), _runtimeConfig, cancellationToken);

            CopyIsolationAssembly(outputDir);

            // Prepare envelope to pass plugin instance and isolation context
            var envelope = new PluginEnvelope(
                PluginType: plugin.GetType().AssemblyQualifiedName!,
                PluginJson: IsolationHelper.Serialize(plugin),
                PluginAssemblyPath: plugin.GetType().Assembly.Location,
                ContextJson: IsolationHelper.Serialize(context)
            );

            var envelopeJson = IsolationHelper.Serialize(envelope);

            var run = await RunProcessAsync(_dotnetFileName, dllPath, outputDir, cancellationToken, stdin: envelopeJson);

            CopyProperties(run.Properties, context.Properties);

            return new PluginExecutionResult(run.StandardOutput, run.StandardError, run.Result);
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    private async Task<(int ExitCode, string StandardOutput, string StandardError, object? Result, Dictionary<string, object> Properties)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            UserName = _userName,
            PasswordInClearText = _password,
            Domain = _domain,
            LoadUserProfile = _loadUserProfile
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) stderr.AppendLine(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        if (!string.IsNullOrWhiteSpace(stdin))
        {
            await process.StandardInput.WriteAsync(stdin);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(CancellationToken.None);

        if (string.IsNullOrWhiteSpace(stdout.ToString()))
        {
            throw new InvalidOperationException("No output received from process.");
        }

        var result = IsolationHelper.Deserialize<ExecutionResult>(stdout.ToString());
        var resultObject = result?.Result;

        if (result?.Result != null && !string.IsNullOrWhiteSpace(result?.ResultType))
        {
            resultObject = IsolationHelper.Deserialize(result.Result, Type.GetType(result.ResultType)!);
        }

        return (process.ExitCode, result!.StandardOutput, result.StandardError, resultObject, result.Properties);
    }
}

public sealed record ExecutionResult(string StandardOutput, string StandardError, object? Result, string? ResultType, Dictionary<string, object> Properties);

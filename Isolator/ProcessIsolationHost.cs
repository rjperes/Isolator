using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Isolator;

public sealed class ProcessIsolationHost : IIsolationHost
{
    private const string _dotnetFileName = "dotnet";
    private const string _runnerName = "Runner";
    private static readonly string _version = new Version(Environment.Version.Major, Environment.Version.Minor, 0).ToString();
    private static readonly string _isolatorAssemblyPath = typeof(IsolationHelper).Assembly.Location;
    private static readonly string? _tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
    private static readonly string _runtimeConfig = $"{{\n \"runtimeOptions\": {{\n \"tfm\": \"net9.0\",\n \"framework\": {{\n \"name\": \"Microsoft.NETCore.App\",\n \"version\": \"{_version}\"\n }},\n \"rollForward\": \"LatestMinor\"\n }}\n}}";
    private static readonly CSharpParseOptions _parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
    private static readonly CSharpCompilationOptions _compilationOptions = new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release);

    private static readonly string[] _requiredReferences =
        [
            typeof(object).Assembly.Location,
            typeof(Console).Assembly.Location,
            typeof(Task).Assembly.Location,
        ];

    private static readonly string _programSource = $$"""
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Isolator;
[assembly:System.CodeDom.Compiler.GeneratedCode("{{typeof(IsolationHelper).Namespace}}", "{{typeof(IsolationHelper).Assembly.GetName().Version?.ToString()}}")]
internal class Program
{
    public static void Main()
    {
        var (plugin, ctx) = {{nameof(IsolationHelper)}}.{{nameof(IsolationHelper.GetBootstrap)}}();
        {{nameof(ExecutionResult)}} response = null;
        using (var stdout = {{nameof(IsolationHelper)}}.{{nameof(IsolationHelper.CaptureOutput)}}())
        using (var stderr = {{nameof(IsolationHelper)}}.{{nameof(IsolationHelper.CaptureError)}}())
        {
            var result = plugin.{{nameof(IPlugin.Execute)}}(ctx);
            var output = stdout.ToString();
            var error = stderr.ToString();
            response = new(
                Result: result,
                ResultType: (result != null) ? result.GetType().FullName : null,
                StandardOutput: output,
                StandardError: error,
                Properties: ctx.Properties
            );
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(response));
    }
}
""";

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value);
    }

    public async Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default) where TPlugin : IPlugin, new()
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);

        var tempDir = Directory.CreateTempSubdirectory($"isolator-runner-{Guid.NewGuid()}");
        try
        {
            // Compile the minimal runner in-memory and emit artifacts to the temp directory
            var (outputDir, dllPath) = await CompileRunnerAsync(tempDir.FullName, cancellationToken);

            // Prepare envelope to pass plugin instance and isolation context
            var envelope = new PluginEnvelope(
                PluginType: plugin.GetType().AssemblyQualifiedName!,
                PluginJson: Serialize(plugin),
                PluginAssemblyPath: plugin.GetType().Assembly.Location,
                ContextJson: Serialize(context)
            );

            var envelopeJson = Serialize(envelope);

            var run = await RunProcessAsync(_dotnetFileName, dllPath, outputDir, cancellationToken, stdin: envelopeJson);

            foreach (var kv in run.Properties)
            {
                context.Properties[kv.Key] = kv.Value;
            }

            return new PluginExecutionResult(run.StandardOutput, run.StandardError, run.Result);
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    private static async Task<(string OutputDir, string DllPath)> CompileRunnerAsync(string outputDir, CancellationToken ct)
    {
        var dllPath = Path.Combine(outputDir, $"{_runnerName}.dll");
        var syntaxTree = CSharpSyntaxTree.ParseText(_programSource, _parseOptions);
        var references = new List<MetadataReference>();

        if (!string.IsNullOrWhiteSpace(_tpa))
        {
            foreach (var path in _tpa!.Split(Path.PathSeparator))
            {
                try
                {
                    var name = Path.GetFileName(path);

                    if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        references.Add(MetadataReference.CreateFromFile(path));
                    }
                }
                catch { }
            }
        }

        // Ensure basic references are present
        foreach (var r in _requiredReferences)
        {
            if (!references.OfType<PortableExecutableReference>().Any(m => string.Equals(m.FilePath, r, StringComparison.OrdinalIgnoreCase)))
            {
                references.Add(MetadataReference.CreateFromFile(r));
            }
        }

        // Reference the Isolator assembly explicitly so the runner can use IsolationHelper and types
        references.Add(MetadataReference.CreateFromFile(_isolatorAssemblyPath));

        var compilation = CSharpCompilation.Create(
            assemblyName: _runnerName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: _compilationOptions);

        await using (var peStream = new MemoryStream())
        {
            var emitResult = compilation.Emit(peStream, cancellationToken: ct);

            if (!emitResult.Success)
            {
                var diag = string.Join(Environment.NewLine, emitResult.Diagnostics.Select(d => d.ToString()));
                throw new InvalidOperationException($"Failed to compile runner: {diag}");
            }

            peStream.Position = 0;
            await using var fs = File.Create(dllPath);
            await peStream.CopyToAsync(fs, ct);
        }

        // Copy Isolator.dll next to runner so runtime can resolve it
        try
        {
            var targetIsolatorPath = Path.Combine(outputDir, Path.GetFileName(_isolatorAssemblyPath));
            if (!File.Exists(targetIsolatorPath))
            {
                File.Copy(_isolatorAssemblyPath, targetIsolatorPath, overwrite: true);
            }
        }
        catch { }

        // Write minimal runtimeconfig.json so 'dotnet Runner.dll' can launch
        await File.WriteAllTextAsync(Path.Combine(outputDir, $"{_runnerName}.runtimeconfig.json"), _runtimeConfig, ct);

        return (outputDir, dllPath);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError, object? Result, Dictionary<string, object> Properties)> RunProcessAsync(
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
            CreateNoWindow = true
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

        var result = JsonSerializer.Deserialize<ExecutionResult>(stdout.ToString());
        var resultObject = result?.Result;

        if (result?.Result is JsonElement && !string.IsNullOrWhiteSpace(result?.ResultType))
        {
            resultObject = JsonSerializer.Deserialize((JsonElement)result.Result, Type.GetType(result.ResultType)!);
        }

        return (process.ExitCode, result!.StandardOutput, result.StandardError, resultObject, result.Properties);
    }

    public void Dispose()
    {
        // No unmanaged resources to dispose
    }
}

public record ExecutionResult(string StandardOutput, string StandardError, object? Result, string? ResultType, Dictionary<string, object> Properties);

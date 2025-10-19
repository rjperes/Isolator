using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Isolator;

public sealed class ProcessIsolationHost : IIsolationHost
{
    public async Task<PluginExecutionResult> ExecutePluginAsync(IPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);

        var tempDir = Directory.CreateTempSubdirectory($"isolator-runner-{Guid.NewGuid()}");
        try
        {
            // Compile the minimal runner in-memory and emit artifacts to the temp directory
            var (outputDir, dllPath) = await CompileRunnerAsync(tempDir.FullName, cancellationToken);
            var fileName = "dotnet";
            var args = dllPath;

            // Prepare envelope to pass plugin instance and isolation context
            var envelope = new PluginEnvelope(
                PluginType: plugin.GetType().AssemblyQualifiedName!,
                PluginJson: JsonSerializer.Serialize(plugin),
                PluginAssemblyPath: plugin.GetType().Assembly.Location,
                ContextJson: JsonSerializer.Serialize(context)
            );
            var envelopeJson = JsonSerializer.Serialize(envelope);

            var run = await RunProcessAsync(fileName, args, outputDir, cancellationToken, stdin: envelopeJson);

            return new PluginExecutionResult(run.ExitCode, run.StandardOutput, run.StandardError);
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    private static string GetCurrentNetCoreAppVersion()
    {
        var v = Environment.Version; // e.g.,9.0.x
        return $"{v.Major}.{v.Minor}.0";
    }

    private static async Task<(string OutputDir, string DllPath)> CompileRunnerAsync(string baseDir, CancellationToken ct)
    {
        var outputDir = baseDir; // emit directly into temp dir
        var dllPath = Path.Combine(outputDir, "Runner.dll");

        var programSource = "" +
           @"using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Isolator;
            internal class Program
            {
             public static async Task<int> Main(string[] args)
             {
             var (plugin, ctx) = IsolationHelper.GetBootstrap();
             return await plugin.ExecuteAsync(ctx, CancellationToken.None);
             }
            }";

        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(programSource, parseOptions);

        var references = new List<MetadataReference>();
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(tpa))
        {
            foreach (var path in tpa!.Split(Path.PathSeparator))
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
        var required = new[]
        {
             typeof(object).Assembly.Location,
             typeof(Console).Assembly.Location,
             typeof(Task).Assembly.Location,
         };
        foreach (var r in required)
        {
            if (!references.OfType<PortableExecutableReference>().Any(m => string.Equals(m.FilePath, r, StringComparison.OrdinalIgnoreCase)))
            {
                references.Add(MetadataReference.CreateFromFile(r));
            }
        }

        // Reference the Isolator assembly explicitly so the runner can use IsolationHelper and types
        var isolatorAssemblyPath = typeof(IsolationHelper).Assembly.Location;
        references.Add(MetadataReference.CreateFromFile(isolatorAssemblyPath));

        var compilation = CSharpCompilation.Create(
        assemblyName: "Runner",
        syntaxTrees: [syntaxTree],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release));

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
            var targetIsolatorPath = Path.Combine(outputDir, Path.GetFileName(isolatorAssemblyPath));
            if (!File.Exists(targetIsolatorPath))
            {
                File.Copy(isolatorAssemblyPath, targetIsolatorPath, overwrite: true);
            }
        }
        catch { }

        // Write minimal runtimeconfig.json so 'dotnet Runner.dll' can launch
        var runtimeConfig = $"{{\n \"runtimeOptions\": {{\n \"tfm\": \"net9.0\",\n \"framework\": {{\n \"name\": \"Microsoft.NETCore.App\",\n \"version\": \"{GetCurrentNetCoreAppVersion()}\"\n }},\n \"rollForward\": \"LatestMinor\"\n }}\n}}";
        await File.WriteAllTextAsync(Path.Combine(outputDir, "Runner.runtimeconfig.json"), runtimeConfig, ct);

        return (outputDir, dllPath);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
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

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

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

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(CancellationToken.None);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public void Dispose()
    {
    }
}
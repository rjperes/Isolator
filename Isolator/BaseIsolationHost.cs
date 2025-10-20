using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Isolator
{
    public abstract class BaseIsolationHost : IIsolationHost
    {
        protected const string _runnerName = "Runner";
        private static readonly string _isolatorAssemblyPath = typeof(IsolationHelper).Assembly.Location;
        private static readonly string? _tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        private static readonly CSharpParseOptions _parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        private static readonly string[] _requiredReferences =
            [
                typeof(object).Assembly.Location,
                typeof(Console).Assembly.Location,
                typeof(Task).Assembly.Location,
                typeof(Microsoft.CSharp.RuntimeBinder.RuntimeBinderException).Assembly.Location
            ];

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            this.Dispose(disposing: true);
        }

        protected virtual void Dispose(bool disposing)
        {
            // Override in derived classes if needed
        }

        protected static async Task<(string OutputDir, string DllPath)> CompileRunnerAsync(string programSource, string outputDir, bool isApp, CancellationToken ct)
        {
            var dllPath = Path.Combine(outputDir, $"{_runnerName}.dll");
            var syntaxTree = CSharpSyntaxTree.ParseText(programSource, _parseOptions);
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

            var compilationOptions = new CSharpCompilationOptions(isApp ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release);

            var compilation = CSharpCompilation.Create(
                assemblyName: _runnerName,
                syntaxTrees: [syntaxTree],
                references: references,
                options: compilationOptions);

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

            return (outputDir, dllPath);
        }

        protected static void CopyIsolationAssembly(string outputDir)
        {
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
        }

        protected static DirectoryInfo CreateTempDirectory()
        {
            var tempDir = Directory.CreateTempSubdirectory($"isolator-runner-{Guid.NewGuid()}");
            return tempDir;
        }

        public static void DeleteTempDirectory(DirectoryInfo tempDir)
        {
            try { tempDir.Delete(recursive: true); } catch { /* best effort cleanup */ }
        }

        public abstract Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default) where TPlugin : IPlugin, new();
    }
}

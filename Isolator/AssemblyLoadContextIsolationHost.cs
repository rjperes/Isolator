using System.Reflection;
using System.Runtime.Loader;

namespace Isolator;

public class AssemblyLoadContextIsolationHost : BaseIsolationHost
{
    private static readonly string _programSource = $$"""
        using System;
        using Isolator;
        [assembly:System.CodeDom.Compiler.GeneratedCode("{{typeof(IsolationHelper).Namespace}}", "{{typeof(IsolationHelper).Assembly.GetName().Version?.ToString()}}")]
        public static class PluginWrapper
        {
            public static object Execute(dynamic plugin, dynamic context)
            {
                return plugin.Execute(context);
            }
        }
        """;

    public override async Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);

        var tempDir = CreateTempDirectory();
        try
        {
            // Compile the minimal runner in-memory and emit artifacts to the temp directory
            var (outputDir, dllPath) = await CompileRunnerAsync(_programSource, tempDir.FullName, false, cancellationToken);

            var alc = new PluginLoadContext(dllPath);
            var asm = alc.LoadFromAssemblyPath(dllPath);
            var type = asm.GetTypes()[0];
            var method = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);

            var originalStdout = Console.Out;
            var originalStderr = Console.Error;

            var stdoutWriter = new StringWriter();
            var stderrWriter = new StringWriter();

            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);

            var result = method!.Invoke(null, [plugin, context]);

            Console.SetOut(originalStdout);
            Console.SetError(originalStderr);

            type = null;
            asm = null;

            alc.Unload();

            return new PluginExecutionResult(
                StandardOutput: stdoutWriter.ToString(),
                StandardError: stderrWriter.ToString(),
                Result: result
            );
        }
        finally
        {
            DeleteTempDirectory(tempDir);
        }
    }

    class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath) : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);

            if (!string.IsNullOrEmpty(path))
            {
                return LoadFromAssemblyPath(path);
            }

            return null;
        }
    }
}

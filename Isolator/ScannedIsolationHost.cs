using Scanner;
using System.Reflection;

namespace Isolator;

public class ScannedIsolationHost : IIsolationHost
{
    private readonly IIsolationHost _isolationHost;
    private readonly IScanner _scanner = new ReferencesScanner();

    public ScannedIsolationHost(IIsolationHost isolationHost)
    {
        ArgumentNullException.ThrowIfNull(isolationHost);
        _isolationHost = isolationHost;
    }

    public HashSet<Assembly> UnsafeAssemblies { get; } = [];
    public HashSet<Type> UnsafeTypes { get; } = [];
    public HashSet<Assembly> SafeAssemblies { get; } = [];
    public HashSet<Type> SafeTypes { get; } = [];

    public void Dispose()
    {
    }

    public Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default) where TPlugin : IPlugin, new()
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);

        if (!PerformCheck(plugin.GetType().Assembly))
        {
            throw new ArgumentException("The plugin contains references to unsafe types or assemblies.");
        }

        return _isolationHost.ExecutePluginAsync(plugin, context, cancellationToken);
    }

    private bool PerformCheck(Assembly assembly)
    {
        var references = _scanner.GetReferences(assembly);

        // if safe assemblies or types are defined, check them first
        if (SafeAssemblies.Count != 0 || SafeTypes.Count != 0)
        {
            if (references.Any(r => SafeTypes.Contains(r.Method.DeclaringType!)) ||
                references.Any(r => SafeAssemblies.Contains(r.Method.DeclaringType!.Assembly)))
            {
                return true;
            }

            // If none of the safe types or assemblies were found, fail the check.
            return false;
        }

        // Now check for unsafe types and assemblies.
        foreach (var reference in references)
        {
            if (UnsafeTypes.Contains(reference.Method.DeclaringType!))
            {
                return false;
            }

            if (UnsafeAssemblies.Contains(reference.Method.DeclaringType!.Assembly))
            {
                return false;
            }
        }

        return true;
    }
}

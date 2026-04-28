using DotNetIsolator;
using System.Text;

namespace Isolator;

public class WasmIsolationHost : IIsolationHost
{
    private IsolatedRuntimeHost? _host;
    private IsolatedRuntime? _runtime;

    public WasmIsolationHost()
    {
        _host = new IsolatedRuntimeHost().WithBinDirectoryAssemblyLoader();
        _runtime = new IsolatedRuntime(_host);
    }

    public void Dispose()
    {
        _host?.Dispose();
        _host = null;
        _runtime?.Dispose();
        _runtime = null;
    }

    public Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default) where TPlugin : IPlugin, new()
    {
        ObjectDisposedException.ThrowIf(_runtime is null, nameof(IsolatedRuntime));

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var capture = Capture.Start(stdout, stderr);

        var result = _runtime.Invoke<object?>(() =>
        {
            return plugin.Execute(context);
        });

        return Task.FromResult(new PluginExecutionResult(stdout.ToString(), stderr.ToString(), result));
    }
}

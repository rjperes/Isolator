namespace Isolator;

public class ClientIsolationHost(string host, uint port) : IIsolationHost
{
    private IsolationHostClient? _client = new();

    public void Dispose()
    {
        _client?.Dispose();
        _client ??= null;
    }

    public async Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default) where TPlugin : IPlugin, new()
    {
        ObjectDisposedException.ThrowIf(_client == null, this);
        var result = await _client.TransmitAsync(host, port, plugin, context, cancellationToken);
        return result;
    }
}

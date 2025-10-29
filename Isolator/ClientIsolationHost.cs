
namespace Isolator
{
    public class ClientIsolationHost(string host, uint port, ISerializer? serializer = null) : IIsolationHost
    {
        private readonly IsolationHostClient _client = new();

        public void Dispose()
        {
        }

        public async Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default) where TPlugin : IPlugin, new()
        {
            var result = await _client.TransmitAsync(host, port, plugin, context, cancellationToken);
            return result;
        }
    }
}

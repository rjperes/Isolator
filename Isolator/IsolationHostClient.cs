namespace Isolator;

public class IsolationHostClient
{
    public ISerializer Serializer { get; init; } = new IsolationJsonSerializer();

    public async Task<PluginExecutionResult> TransmitAsync(string host, uint port, IPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);

        if (port < 1024 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1024 and 65535.");
        }

        var pluginType = plugin.GetType();

        if (!pluginType.IsPublic || !pluginType.IsClass || pluginType.IsAbstract || pluginType.IsGenericType)
        {
            throw new ArgumentException($"Type '{pluginType.FullName}' must be a public non-abstract, non-generic class.", nameof(plugin));
        }

        using var transmitter = new TcpTransmitter
        {
            Serializer = Serializer
        };

        return await transmitter.TransmitAsync(host, (int)port, pluginType.Assembly, pluginType, plugin, context, cancellationToken);
    }
}

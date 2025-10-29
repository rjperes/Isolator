namespace Isolator;

public class IsolationHostClient : IDisposable
{
    private ITransmitter? _transmitter;
    private bool _selfTransmitter;

    public ITransmitter Transmitter
    {
        get
        {
            if (_transmitter == null)
            {
                _transmitter = new TcpTransmitter();
                _selfTransmitter = true;
            }
            return _transmitter;
        }
        init
        {
            _transmitter = value;
            _selfTransmitter = false;
        }
    }

    public async Task<PluginExecutionResult> TransmitAsync(string host, uint port, IPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_transmitter == null, this);
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

        return await Transmitter.TransmitAsync(host, (int)port, pluginType.Assembly, pluginType, plugin, context, cancellationToken);
    }

    public void Dispose()
    {
        if (_selfTransmitter)
        {
            _transmitter?.Dispose();
            _transmitter = null;
        }
    }
}

namespace Isolator;

public class IsolationHostServer : IDisposable
{
    private CancellationTokenSource? _cts;

    public ISerializer Serializer { get; init; } = new IsolationJsonSerializer();

    public async Task ReceiveAsync(uint port, CancellationToken cancellationToken)
    {
        if (port < 1024 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1024 and 65535.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using var listener = new TcpReceiver
        {
            Serializer = Serializer
        };

        await listener.ReceiveAsync(port, _cts.Token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts ??= null;
    }
}

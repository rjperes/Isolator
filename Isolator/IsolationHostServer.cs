namespace Isolator;

public class IsolationHostServer : IDisposable
{
    private CancellationTokenSource? _cts;

    private IReceiver? _receiver;
    private bool _selfReceiver;

    public IReceiver Receiver
    {
        get
        {
            if (_receiver == null)
            {
                _receiver = new TcpReceiver();
                _selfReceiver = true;
            }
            return _receiver;
        }
        init
        {
            _receiver = value;
            _selfReceiver = false;
        }
    }

    public async Task ReceiveAsync(uint port, CancellationToken cancellationToken)
    {
        if (port < 1024 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1024 and 65535.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        await Receiver.ReceiveAsync(port, _cts.Token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts ??= null;
        if (_selfReceiver)
        {
            _receiver?.Dispose();
            _receiver = null;
        }
    }
}

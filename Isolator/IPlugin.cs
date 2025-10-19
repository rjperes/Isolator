namespace Isolator;

public interface IPlugin
{
    Task<int> ExecuteAsync(IsolationContext ctx, CancellationToken cancellationToken = default);
}
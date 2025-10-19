namespace Isolator;

public interface IIsolationHost : IDisposable
{
    Task<PluginExecutionResult> ExecutePluginAsync(IPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default);
}

public record PluginExecutionResult(int ExitCode, string StandardOutput, string StandardError);
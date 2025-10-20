namespace Isolator;

public interface IIsolationHost : IDisposable
{
    Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default) where TPlugin : IPlugin, new();
}

public record PluginExecutionResult(string StandardOutput, string StandardError, object? Result);
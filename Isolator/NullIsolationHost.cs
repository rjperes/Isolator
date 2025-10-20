namespace Isolator;

public sealed class NullIsolationHost : IIsolationHost
{
    public void Dispose()
    {
    }

    public Task<PluginExecutionResult> ExecutePluginAsync<TPlugin>(TPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default) where TPlugin : IPlugin, new()
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);

        var originalStdout = Console.Out;
        var originalStderr = Console.Error;

        var stdoutWriter = new StringWriter();
        var stderrWriter = new StringWriter();

        Console.SetOut(stdoutWriter);
        Console.SetError(stderrWriter);

        var result = plugin.Execute(context);

        Console.SetOut(originalStdout);
        Console.SetError(originalStderr);

        return Task.FromResult(new PluginExecutionResult(
            Result: result,
            StandardOutput: stdoutWriter.ToString(),
            StandardError: stderrWriter.ToString()
        ));
    }
}

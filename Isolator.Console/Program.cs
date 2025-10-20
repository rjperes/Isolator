namespace Isolator.Console;

internal class Program
{
    static async Task<PluginExecutionResult> TestUsingAssemblyLoadContextIsolationHost<TPlugin>(TPlugin plugin, IsolationContext context) where TPlugin : IPlugin, new()
    {
        using var host = new AssemblyLoadContextIsolationHost();
        var res = await host.ExecutePluginAsync(plugin, context);
        return res;
    }

    static async Task<PluginExecutionResult> TestUsingProcessIsolationHost<TPlugin>(TPlugin plugin, IsolationContext context) where TPlugin : IPlugin, new()
    {
        using var host = new ProcessIsolationHost();
        var res = await host.ExecutePluginAsync(plugin, context);
        return res;
    }

    static async Task<PluginExecutionResult> TestUsingNullIsolationHost<TPlugin>(TPlugin plugin, IsolationContext context) where TPlugin : IPlugin, new()
    {
        using var host = new NullIsolationHost();
        var res = await host.ExecutePluginAsync(plugin, context);
        return res;
    }

    static async Task Main(string[] args)
    {
        var plugin = new HelloWorldPlugin();
        var context = new IsolationContext
        {
            Properties = new Dictionary<string, object>
            {
                ["Greeting"] = "Hello, World!"
            },
            Arguments = ["This", "is", "a", "test"]
        };

        var res1 = await TestUsingAssemblyLoadContextIsolationHost(plugin, context);
        var res2 = await TestUsingProcessIsolationHost(plugin, context);
        var res3 = await TestUsingNullIsolationHost(plugin, context);
    }
}

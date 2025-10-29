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

    static async Task<PluginExecutionResult> TestUsingClientIsolationHost<TPlugin>(TPlugin plugin, IsolationContext context) where TPlugin : IPlugin, new()
    {
        using var host = new ClientIsolationHost("localhost", 5000);
        var res = await host.ExecutePluginAsync(plugin, context);
        return res;
    }

    static async Task Main(string[] args)
    {
        using var server = new IsolationHostServer();
        var client = new IsolationHostClient();
        var context = new IsolationContext
        {
            Properties = new Dictionary<string, object>
            {
                ["Greeting"] = "Hello, World!"
            },
            Arguments = ["This", "is", "a", "test"]
        };

        using var evt = new ManualResetEventSlim(false);

        ThreadPool.QueueUserWorkItem(async _ =>
        {
            evt.Set();
            await server.ReceiveAsync(5000, CancellationToken.None);
        });

        var plugin = new HelloWorldPlugin();
        
        evt.Wait();

        //var res1 = await TestUsingAssemblyLoadContextIsolationHost(plugin, context);
        //var res2 = await TestUsingProcessIsolationHost(plugin, context);
        //var res3 = await TestUsingNullIsolationHost(plugin, context);
        var res4 = await TestUsingClientIsolationHost(plugin, context);
    }
}

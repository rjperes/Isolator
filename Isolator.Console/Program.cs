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

    static async Task Main()
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

        ThreadPool.QueueUserWorkItem(async _ =>
        {
            await server.ReceiveAsync(5000, CancellationToken.None);
        });

        var plugin = new HelloWorldPlugin();

        //var res1 = await TestUsingAssemblyLoadContextIsolationHost(plugin, context);
        //var res2 = await TestUsingProcessIsolationHost(plugin, context);
        //var res3 = await TestUsingNullIsolationHost(plugin, context);

        //wait for the server to start
        Thread.Sleep(3000);

        var res4 = await TestUsingClientIsolationHost(plugin, context);

        System.Console.ReadLine();
    }
}

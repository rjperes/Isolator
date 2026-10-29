using Isolator.Mxc;
using Microsoft.Mxc.Sdk.V1;

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

    static async Task<PluginExecutionResult> TestUsingDockerIsolationHost<TPlugin>(TPlugin plugin, IsolationContext context) where TPlugin : IPlugin, new()
    {
        using var host = new DockerIsolationHost();
        var res = await host.ExecutePluginAsync(plugin, context);
        return res;
    }

    static async Task<PluginExecutionResult?> TestUsingMxcIsolationHost<TPlugin>(TPlugin plugin, IsolationContext context) where TPlugin : IPlugin, new()
    {
        var platformSupport = MxcPlatform.GetPlatformSupport();
        if (!platformSupport.IsSupported)
        {
            System.Console.Error.WriteLine($"MXC is unavailable: {platformSupport.Reason}");
            return null;
        }

        using var host = new MxcIsolationHost();
        var res = await host.ExecutePluginAsync(plugin, context);
        return res;
    }

    static async Task Main()
    {
        var context = new IsolationContext
        {
            Properties = new Dictionary<string, object>
            {
                ["Greeting"] = "Hello, World!"
            },
            Arguments = ["This", "is", "a", "test"]
        };

        var plugin = new HelloWorldPlugin();

        //var res1 = await TestUsingAssemblyLoadContextIsolationHost(plugin, context);
        //var res2 = await TestUsingProcessIsolationHost(plugin, context);
        //var res3 = await TestUsingNullIsolationHost(plugin, context);
        //var res4 = await TestUsingClientIsolationHost(plugin, context);
        //var res5 = await TestUsingDockerIsolationHost(plugin, context);
        var res6 = await TestUsingMxcIsolationHost(plugin, context);

        System.Console.ReadLine();
    }
}

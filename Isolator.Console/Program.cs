namespace Isolator.Console;

internal class Program
{
    static async Task Main(string[] args)
    {
        using var host = new ProcessIsolationHost();
        var res = await host.ExecutePluginAsync(new HelloWorldPlugin(), new IsolationContext
        {
            Properties = new Dictionary<string, object>
            {
                ["Greeting"] = "Hello, World!"
            },
            Arguments = ["This", "is", "a", "test"]
        });
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Isolator.Docker;

internal class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("No plugin specified.");
            return -1;
        }

        var (plugin, context) = GetBootstrap(args);
        var (stdout, stderr, result, properties) = RunPlugin(plugin, context);
        var executionResult = new ExecutionResult(stdout, stderr, result, result?.GetType()?.FullName, context.Properties);

        Console.WriteLine(IsolationHelper.Serialize(executionResult));

        return 0;
    }

    private static (IPlugin, IsolationContext) GetBootstrap(string[] args)
    {
        var pluginTypeParts = args[0].Split(',');
        var pluginTypeName = pluginTypeParts[0].Trim();
        var pluginTypeAssemblyName = pluginTypeParts[1].Trim() + ".dll";

        var pluginTypeAssembly = Assembly.LoadFrom(pluginTypeAssemblyName);
        var pluginType = pluginTypeAssembly.GetType(pluginTypeName);

        var plugin = (IPlugin)Activator.CreateInstance(pluginType!)!;

        var contextJson = args[1];
        var context = IsolationHelper.Deserialize<IsolationContext>(contextJson);

        return (plugin, context);
    }

    private static (string standardOutput, string standardError, object? result, object properties) RunPlugin(IPlugin plugin, IsolationContext context)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var process = Process.GetCurrentProcess();

        var oldStdout = Console.Out;
        var oldStderr = Console.Error;

        Console.SetOut(new StringWriter(stdout));
        Console.SetError(new StringWriter(stderr));

        var result = plugin.Execute(context);

        Console.SetOut(oldStdout);
        Console.SetError(oldStderr);

        return (stdout.ToString(), stderr.ToString(), result, context.Properties);
    }
}

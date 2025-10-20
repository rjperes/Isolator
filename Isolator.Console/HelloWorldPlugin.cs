namespace Isolator.Console
{
    public class HelloWorldPlugin : IPlugin
    {
        public object? Execute(IsolationContext ctx)
        {
            System.Console.WriteLine(ctx.Properties["Greeting"]);
            System.Console.WriteLine(string.Join(", ", ctx.Arguments));
            ctx.Properties["ExecutedAt"] = DateTime.UtcNow;
            return DateTime.UtcNow;
        }
    }
}

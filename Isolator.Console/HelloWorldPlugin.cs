namespace Isolator.Console
{
    public class HelloWorldPlugin : IPlugin
    {
        public Task<int> ExecuteAsync(IsolationContext ctx, CancellationToken cancellationToken = default)
        {
            System.Console.WriteLine(ctx.Properties["Greeting"]);
            return Task.FromResult(0);
        }
    }
}

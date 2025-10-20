namespace Isolator;

public interface IPlugin
{
    object Execute(IsolationContext ctx);
}
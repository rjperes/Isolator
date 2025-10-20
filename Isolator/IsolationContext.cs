namespace Isolator;

public class IsolationContext
{
    public Dictionary<string, object> Properties { get; set; } = [];
    public string[] Arguments { get; set; } = [];
}
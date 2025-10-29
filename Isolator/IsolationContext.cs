namespace Isolator;

public sealed record IsolationContext
{
    public Dictionary<string, object> Properties { get; set; } = [];
    public string[] Arguments { get; set; } = [];
}
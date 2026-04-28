using System.Text;

namespace Isolator;

public sealed class Capture : IDisposable
{
    private readonly TextWriter _oldOut;
    private readonly TextWriter? _oldErr;

    private Capture(StringBuilder stdout, StringBuilder? stderr)
    {
        _oldOut = Console.Out;
        Console.SetOut(new StringWriter(stdout));

        if (stderr != null)
        {
            _oldErr = Console.Error;
            Console.SetError(new StringWriter(stderr));
        }
    }

    public static IDisposable Start(StringBuilder stdout, StringBuilder? stderr)
    {
        return new Capture(stdout, stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_oldOut);
        if (_oldErr != null)
        {
            Console.SetError(_oldErr);
        }
    }
}

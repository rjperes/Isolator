using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;

namespace Isolator;

public class IsolationHostServer : IDisposable
{
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts;

    public async Task ReceiveAsync(uint port, CancellationToken cancellationToken)
    {
        if (port < 1024 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1024 and 65535.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, (int)port);
        _listener.Start();

        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            await HandleClient(client);
        }
    }

    public void Dispose()
    {
         _cts.Cancel();
        _listener?.Stop();
        _listener ??= null;
        _cts.Dispose();
    }

    private async Task HandleClient(TcpClient client)
    {
        using var ns = client.GetStream();
        using var br = new BinaryReader(ns, Encoding.UTF8, leaveOpen: true);
        using var bw = new BinaryWriter(ns, Encoding.UTF8, leaveOpen: true);

        try
        {
            // 1) read assembly bytes
            var assemblyLength = br.ReadInt32();
            var assemblyBytes = br.ReadBytes(assemblyLength);

            // 2) read type name
            var typeNameLength = br.ReadInt32();
            var typeName = Encoding.UTF8.GetString(br.ReadBytes(typeNameLength));

            // 3) read plugin
            var pluginLength = br.ReadInt32();
            var pluginString = Encoding.UTF8.GetString(br.ReadBytes(pluginLength));

            // 4) read context
            var contextLength = br.ReadInt32();
            var contextString = Encoding.UTF8.GetString(br.ReadBytes(contextLength));
            var context = IsolationHelper.Deserialize<IsolationContext>(contextString);

            // Load into a new context from bytes
            var asc = new AssemblyLoadContext("IsolationHostContext", isCollectible: true);
            var assembly = asc.LoadFromStream(new MemoryStream(assemblyBytes));

            // Resolve type and method
            var type = assembly.GetType(typeName, throwOnError: false);

            if (type == null)
            {
                var error = $"Type '{typeName}' not found in assembly.";
                WriteString(bw, error);
                return;
            }

            if (!typeof(IPlugin).IsAssignableFrom(type))
            {
                var error = $"Type '{typeName}' is not of a plugin type.";
                WriteString(bw, error);
                return;
            }

            if (!type.IsPublic || !type.IsClass || type.IsAbstract || type.IsGenericType)
            {
                var error = $"Type '{typeName}' is not of a public non-abstract, non-generic plugin type.";
                WriteString(bw, error);
                return;
            }

            var instance = IsolationHelper.Deserialize(pluginString, type) as IPlugin;

            if (instance == null)
            {
                var error = $"Type '{typeName}' does not implement IPlugin interface.";
                WriteString(bw, error);
                return;
            }

            var originalStdout = Console.Out;
            var originalStderr = Console.Error;

            var stdoutWriter = new StringWriter();
            var stderrWriter = new StringWriter();

            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);

            var result = instance.Execute(context!);

            Console.SetOut(originalStdout);
            Console.SetError(originalStderr);

            var resultString = IsolationHelper.Serialize(result);

            // send back result
            WriteString(bw, result?.GetType().AssemblyQualifiedName + ":" + resultString);
            WriteString(bw, stdoutWriter.ToString());
            WriteString(bw, stderrWriter.ToString());

            instance = null;
            type = null;
            assembly = null;

            asc.Unload();
        }
        catch (Exception ex)
        {
            WriteString(bw, $"ERROR: {ex.Message}");
        }
    }

    private void WriteString(BinaryWriter bw, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        bw.Write(bytes.Length);
        bw.Write(bytes);
        bw.Flush();
    }
}

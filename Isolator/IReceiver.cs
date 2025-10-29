using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;

namespace Isolator;

public record ClientMessage(byte[] AssemblyBytes, string PluginTypeName, string Plugin, string Context);

public interface IReceiver : IDisposable
{
    Task ReceiveAsync(uint port, CancellationToken cancellationToken);
}

public class TcpReceiver : IReceiver
{
    private TcpListener? _listener;

    public ISerializer Serializer { get; init; } = new IsolationJsonSerializer();

    public async Task ReceiveAsync(uint port, CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Any, (int)port);
        _listener.Start();

        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            await HandleClient(client);
        }
    }

    public void Dispose()
    {
        _listener?.Stop();
        _listener ??= null;
    }

    private async Task HandleClient(TcpClient client)
    {
        using var ns = client.GetStream();
        using var br = new BinaryReader(ns, Encoding.UTF8, leaveOpen: true);
        using var bw = new BinaryWriter(ns, Encoding.UTF8, leaveOpen: true);

        try
        {
            var messageBytes = ReadBytes(br);
            var messageString = Encoding.UTF8.GetString(messageBytes);
            var message = Serializer != null ? Serializer.Deserialize<ClientMessage>(messageString) : IsolationHelper.Deserialize<ClientMessage>(messageString);

            // Load into a new context from bytes
            var asc = new AssemblyLoadContext("IsolationHostContext", isCollectible: true);
            var assembly = asc.LoadFromStream(new MemoryStream(message.AssemblyBytes));

            // Resolve type and method
            var pluginType = assembly.GetType(message.PluginTypeName, throwOnError: false);

            if (pluginType == null)
            {
                var error = $"Type '{message.PluginTypeName}' not found in assembly.";
                WriteString(bw, error);
                return;
            }

            if (!typeof(IPlugin).IsAssignableFrom(pluginType))
            {
                var error = $"Type '{pluginType.FullName}' is not of a plugin type.";
                WriteString(bw, error);
                return;
            }

            if (!pluginType.IsPublic || !pluginType.IsClass || pluginType.IsAbstract || pluginType.IsGenericType)
            {
                var error = $"Type '{pluginType.FullName}' is not of a public non-abstract, non-generic plugin type.";
                WriteString(bw, error);
                return;
            }

            var instance = IsolationHelper.Deserialize(message.Plugin, pluginType) as IPlugin;

            if (instance == null)
            {
                var error = $"Type '{pluginType.FullName}' does not implement IPlugin interface.";
                WriteString(bw, error);
                return;
            }

            var originalStdout = Console.Out;
            var originalStderr = Console.Error;

            var stdoutWriter = new StringWriter();
            var stderrWriter = new StringWriter();

            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);

            var context = Serializer != null ? Serializer.Deserialize<IsolationContext>(message.Context) : IsolationHelper.Deserialize<IsolationContext>(message.Context);

            var result = instance.Execute(context);

            Console.SetOut(originalStdout);
            Console.SetError(originalStderr);

            var resultString = Serializer != null ? Serializer.Serialize(result) : IsolationHelper.Serialize(result);

            var properties = Serializer != null ? Serializer.Serialize(context.Properties) : IsolationHelper.Serialize(context.Properties);

            var response = new ServerMessage
            (
                ResultTypeName: result?.GetType().AssemblyQualifiedName!,
                Result: resultString,
                Stdout: stdoutWriter.ToString(),
                Stderr: stderrWriter.ToString(),
                Properties: properties
            );

            var responseString = Serializer != null ? Serializer.Serialize(response) : IsolationHelper.Serialize(response);

            WriteString(bw, responseString);

            instance = null;
            pluginType = null;
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

    private byte[] ReadBytes(BinaryReader br)
    {
        var length = br.ReadInt32();
        return br.ReadBytes(length);
    }
}
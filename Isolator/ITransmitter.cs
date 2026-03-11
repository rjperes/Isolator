using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Isolator;

public record ServerMessage(string ResultTypeName, string Result, string Stdout, string Stderr, string Properties);

public interface ITransmitter : IDisposable
{
    Task<PluginExecutionResult> TransmitAsync(string host, int port, Assembly assembly, Type pluginType, IPlugin plugin, IsolationContext context, CancellationToken cancellationToken);
}

public class TcpTransmitter : ITransmitter
{
    public ISerializer Serializer { get; init; } = new IsolationJsonSerializer();

    public void Dispose()
    {
    }

    public async Task<PluginExecutionResult> TransmitAsync(string host, int port, Assembly assembly, Type pluginType, IPlugin plugin, IsolationContext context, CancellationToken cancellationToken)
    {
        var dllPath = pluginType.Assembly.Location;

        var assemblyBytes = File.ReadAllBytes(dllPath);

        using var client = new TcpClient();
        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);

        if (addresses == null || addresses.Length == 0)
        {
            throw new ArgumentException($"Invalid host {host}.", nameof(host));
        }

        await client.ConnectAsync(addresses[0], (int)port, cancellationToken);
        using var ns = client.GetStream();
        using var bw = new BinaryWriter(ns, Encoding.UTF8, leaveOpen: true);
        using var br = new BinaryReader(ns, Encoding.UTF8, leaveOpen: true);

        var message = new ClientMessage(
            AssemblyBytes: assemblyBytes,
            PluginTypeName: pluginType.FullName!,
            Plugin: Serializer != null ? Serializer.Serialize(plugin) : IsolationHelper.Serialize(plugin),
            Context: Serializer != null ? Serializer.Serialize(context) : IsolationHelper.Serialize(context)
        );

        var messageString = Serializer != null ? Serializer.Serialize(message) : IsolationHelper.Serialize(message);

        WriteString(bw, messageString);

        // Wait for response (length-prefixed)
        var responseBytes = ReadBytes(br);
        var responseString = Encoding.UTF8.GetString(responseBytes);
        var response = Serializer != null ? Serializer.Deserialize<ServerMessage>(responseString) : IsolationHelper.Deserialize<ServerMessage>(responseString);

        var responseResultType = Type.GetType(response.ResultTypeName, throwOnError: false);

        var responseResult = Serializer != null
                ? Serializer.Deserialize(response.Result, responseResultType!)
                : IsolationHelper.Deserialize(response.Result, responseResultType!);

        return new PluginExecutionResult
        (
            StandardOutput: response.Stdout,
            StandardError: response.Stderr,
            Result: responseResult
        );
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
        ReadOnlySpan<byte> _errorPrefix = new byte[] { 69, 82, 82, 79, 82, 58, 32 }.AsSpan();
        var length = br.ReadInt32();
        var message = br.ReadBytes(length);
        if (message.AsSpan().StartsWith(_errorPrefix))
        {
            var errorMessage = Encoding.UTF8.GetString(message);
            throw new InvalidOperationException(errorMessage.Substring(8));
        }
        return message;
    }
}
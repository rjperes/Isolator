using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Isolator;

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
        var addresses = Dns.GetHostAddresses(host);

        var ipv4Address = addresses.SingleOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);

        if (addresses == null || ipv4Address == null)
        {
            throw new ArgumentException($"Invalid host {host}.", nameof(host));
        }

        await client.ConnectAsync(ipv4Address, (int)port, cancellationToken);
        using var ns = client.GetStream();
        using var bw = new BinaryWriter(ns, Encoding.UTF8, leaveOpen: true);
        using var br = new BinaryReader(ns, Encoding.UTF8, leaveOpen: true);

        // 1) assembly length + bytes
        bw.Write(assemblyBytes.Length);
        bw.Write(assemblyBytes);

        // 2) type name
        var typeNameBytes = Encoding.UTF8.GetBytes(pluginType.FullName!);
        bw.Write(typeNameBytes.Length);
        bw.Write(typeNameBytes);

        // 3) plugin
        var pluginString = Serializer != null ? Serializer.Serialize(plugin) : IsolationHelper.Serialize(plugin);
        var pluginBytes = Encoding.UTF8.GetBytes(pluginString);
        bw.Write(pluginBytes.Length);
        bw.Write(pluginBytes);

        // 4) context
        var contextString = Serializer != null ? Serializer.Serialize(context) : IsolationHelper.Serialize(context);
        var contextBytes = Encoding.UTF8.GetBytes(contextString);
        bw.Write(contextBytes.Length);
        bw.Write(contextBytes);

        bw.Flush();

        // Wait for response (length-prefixed)
        var responseLength = br.ReadInt32();
        var responseBytes = br.ReadBytes(responseLength);
        var responseString = Encoding.UTF8.GetString(responseBytes);
        var responseSeparatorIndex = responseString.IndexOf(':');
        var responseTypeString = responseString.Substring(0, responseSeparatorIndex);
        var responseType = Type.GetType(responseTypeString!, throwOnError: false);
        var responseJson = responseString.Substring(responseSeparatorIndex + 1);
        var response = Serializer != null ? Serializer.Deserialize(responseJson, responseType!) : IsolationHelper.Deserialize(responseJson, responseType!);

        // Wait for stdout
        var stdoutLength = br.ReadInt32();
        var stdoutBytes = br.ReadBytes(stdoutLength);

        // Wait for stderr
        var stderrLength = br.ReadInt32();
        var stderrBytes = br.ReadBytes(stderrLength);

        // Wait for properties
        var propertiesLength = br.ReadInt32();
        var propertiesBytes = br.ReadBytes(propertiesLength);
        var properties = Serializer != null ? Serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(propertiesBytes)) : IsolationHelper.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(propertiesBytes));

        context.Properties = properties!;

        return new PluginExecutionResult
        (
            StandardOutput: Encoding.UTF8.GetString(stdoutBytes),
            StandardError: Encoding.UTF8.GetString(stderrBytes),
            Result: response
        );
    }
}
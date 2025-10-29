using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Isolator;

public class IsolationHostClient
{
    public ISerializer Serializer { get; init; } = new IsolationJsonSerializer();

    public async Task<PluginExecutionResult> TransmitAsync(string host, uint port, IPlugin plugin, IsolationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);

        if (port < 1024 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1024 and 65535.");
        }

        var pluginType = plugin.GetType();

        if (!pluginType.IsPublic || !pluginType.IsClass || pluginType.IsAbstract || pluginType.IsGenericType)
        {
            throw new ArgumentException($"Type '{pluginType.FullName}' must be a public non-abstract, non-generic class.", nameof(pluginType));
        }

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

        return new PluginExecutionResult
        (
            StandardOutput: Encoding.UTF8.GetString(stdoutBytes),
            StandardError: Encoding.UTF8.GetString(stderrBytes),
            Result: response
        );
    }
}

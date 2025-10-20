using System.Reflection;
using System.Text.Json;

namespace Isolator;

public static class IsolationHelper
{
    public static RestoreOutput CaptureOutput()
    {
        return Capture(isError: false);
    }

    public static RestoreOutput CaptureError()
    {
        return Capture(isError: true);
    }

    private static RestoreOutput Capture(bool isError = false)
    {
        var original = isError ? Console.Error : Console.Out;
        var writer = new StringWriter();
        Action action = () => { };

        if (isError)
        {
            Console.SetError(writer);
            action = () => Console.SetError(original);
        }
        else
        {
            Console.SetOut(writer);
            action = () => Console.SetOut(original);
        }

        return new RestoreOutput(original, writer, action);
    }

    public static (IPlugin, IsolationContext) GetBootstrap()
    {
        // Read an envelope with the plugin's assembly-qualified type name and its JSON payload
        using var reader = new StreamReader(Console.OpenStandardInput());
        var raw = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("No plugin data received on standard input.");

        var envelope = JsonSerializer.Deserialize<PluginEnvelope>(raw)
        ?? throw new InvalidOperationException("Failed to parse plugin envelope.");

        // If an assembly path is provided, try to load it to help resolve the plugin type
        if (!string.IsNullOrWhiteSpace(envelope.PluginAssemblyPath) && File.Exists(envelope.PluginAssemblyPath))
        {
            try { _ = Assembly.LoadFrom(envelope.PluginAssemblyPath); } catch { /* ignore load errors */ }
        }

        // Resolve the plugin type
        var pluginType = Type.GetType(envelope.PluginType, throwOnError: false);
        if (pluginType is null && !string.IsNullOrWhiteSpace(envelope.PluginAssemblyPath) && File.Exists(envelope.PluginAssemblyPath))
        {
            try
            {
                var asm = Assembly.LoadFrom(envelope.PluginAssemblyPath);
                pluginType = asm.GetType(envelope.PluginType.Split(',')[0], throwOnError: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Plugin type not found: {envelope.PluginType}", ex);
            }
        }
        if (pluginType is null)
        {
            throw new InvalidOperationException($"Plugin type not found: {envelope.PluginType}");
        }

        var pluginObj = JsonSerializer.Deserialize(envelope.PluginJson, pluginType)
               ?? throw new InvalidOperationException("Failed to deserialize plugin instance.");

        // Deserialize and cache the IsolationContext (if provided), otherwise create a new one
        var context = !string.IsNullOrWhiteSpace(envelope.ContextJson)
            ? (JsonSerializer.Deserialize<IsolationContext>(envelope.ContextJson) ?? new IsolationContext())
            : new IsolationContext();

        return ((IPlugin)pluginObj, context);
    }
}

public class RestoreOutput(TextWriter original, StringWriter writer, Action action) : IDisposable
{
    public void Dispose()
    {
        action();
    }

    public override string ToString() => writer.ToString();
    public TextWriter Original => original;
}

sealed record PluginEnvelope(string PluginType, string PluginJson, string PluginAssemblyPath, string ContextJson);

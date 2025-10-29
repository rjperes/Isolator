using System;
using System.Reflection;
using System.Text.Json;

namespace Isolator;

public interface ISerializer
{
    string Serialize<T>(T value);
    T Deserialize<T>(string json);
    object? Deserialize(object obj, Type type);
    object? Deserialize(string json, Type type);
}

public class JsonSerializer : ISerializer
{
    public string Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value);
    }

    public T Deserialize<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidOperationException("Failed to deserialize JSON.");
    }

    public object? Deserialize(object obj, Type type)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (obj is JsonElement)
        {
            return JsonSerializer.Deserialize((JsonElement)obj, type);
        }
        return Deserialize(obj.ToString() ?? string.Empty, type);
    }

    public object? Deserialize(string json, Type type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(type);
        return JsonSerializer.Deserialize(json, type)
            ?? throw new InvalidOperationException("Failed to deserialize plugin instance.");
    }
}

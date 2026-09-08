using System;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Reads and writes the explicit float fields of MCP vectors, rotations and matrices.
/// Default property-only JSON conversion otherwise silently constructs zero vectors.
/// </summary>
internal sealed class McpNumericsJsonConverter<T> : JsonConverter<T> where T : struct
{
    private static readonly FieldInfo[] Fields = GetFields();

    private static FieldInfo[] GetFields()
    {
        Type type = typeof(T);
        if (type != typeof(Vector2) && type != typeof(Vector3) && type != typeof(Vector4) &&
            type != typeof(Quaternion) && type != typeof(Matrix4x4))
            throw new NotSupportedException($"{type.Name} is not a supported MCP numeric structure.");
        return type.GetFields(BindingFlags.Instance | BindingFlags.Public);
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"{typeof(T).Name} requires an object with all numeric components.");

        // MCP argument conversion is outside engine update/render hot paths.
        object value = default(T);
        uint present = 0u;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected a numeric component name.");
            string name = reader.GetString()!;
            int index = 0;
            while (index < Fields.Length && !string.Equals(Fields[index].Name, name, StringComparison.OrdinalIgnoreCase))
                index++;
            if (index == Fields.Length || (present & (1u << index)) != 0u)
                throw new JsonException($"Unknown or duplicate {typeof(T).Name} component '{name}'.");
            if (!reader.Read())
                throw new JsonException("Missing numeric component value.");
            float component = JsonSerializer.Deserialize<float>(ref reader, options);
            if (!float.IsFinite(component))
                throw new JsonException("Numeric components must be finite.");
            Fields[index].SetValue(value, component);
            present |= 1u << index;
        }
        if (reader.TokenType != JsonTokenType.EndObject || present != (1u << Fields.Length) - 1u)
            throw new JsonException($"{typeof(T).Name} requires every numeric component.");
        return (T)value;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        object boxed = value;
        writer.WriteStartObject();
        foreach (FieldInfo field in Fields)
            writer.WriteNumber(options.PropertyNamingPolicy?.ConvertName(field.Name) ?? field.Name,
                (float)field.GetValue(boxed)!);
        writer.WriteEndObject();
    }
}

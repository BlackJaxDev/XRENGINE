using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Data.Vectors;

namespace XREngine.Editor.Mcp;

/// <summary>Reads and writes the explicit integer X/Y/Z fields of an <see cref="IVector3"/>.</summary>
internal sealed class McpIVector3JsonConverter : JsonConverter<IVector3>
{
    public override IVector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("IVector3 requires an object with X, Y, and Z integer components.");

        int x = 0;
        int y = 0;
        int z = 0;
        uint present = 0u;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected an IVector3 component name.");

            string name = reader.GetString()!;
            uint bit = name switch
            {
                var component when string.Equals(component, "X", StringComparison.OrdinalIgnoreCase) => 1u,
                var component when string.Equals(component, "Y", StringComparison.OrdinalIgnoreCase) => 2u,
                var component when string.Equals(component, "Z", StringComparison.OrdinalIgnoreCase) => 4u,
                _ => throw new JsonException($"Unknown IVector3 component '{name}'."),
            };
            if ((present & bit) != 0u)
                throw new JsonException($"Duplicate IVector3 component '{name}'.");
            if (!reader.Read())
                throw new JsonException("Missing IVector3 component value.");

            int value = JsonSerializer.Deserialize<int>(ref reader, options);
            switch (bit)
            {
                case 1u: x = value; break;
                case 2u: y = value; break;
                default: z = value; break;
            }
            present |= bit;
        }

        if (reader.TokenType != JsonTokenType.EndObject || present != 7u)
            throw new JsonException("IVector3 requires X, Y, and Z integer components.");
        return new IVector3(x, y, z);
    }

    public override void Write(Utf8JsonWriter writer, IVector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(options.PropertyNamingPolicy?.ConvertName("X") ?? "X", value.X);
        writer.WriteNumber(options.PropertyNamingPolicy?.ConvertName("Y") ?? "Y", value.Y);
        writer.WriteNumber(options.PropertyNamingPolicy?.ConvertName("Z") ?? "Z", value.Z);
        writer.WriteEndObject();
    }
}

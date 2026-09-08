using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Data.Colors;

namespace XREngine.Editor.Mcp;

/// <summary>Converts MCP color arguments without reflecting ColorF4's unsafe pointer members.</summary>
internal sealed class McpColorF4JsonConverter : JsonConverter<ColorF4>
{
    public override ColorF4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string hex = reader.GetString()!.TrimStart('#');
            if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgba))
                throw new JsonException("Expected #RRGGBB or #RRGGBBAA.");
            if (hex.Length == 6)
                rgba = (rgba << 8) | 255u;
            return new ColorF4((rgba >> 24) / 255f, ((rgba >> 16) & 255u) / 255f,
                ((rgba >> 8) & 255u) / 255f, (rgba & 255u) / 255f);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a color object with R, G, B and optional A, or a hex string.");

        ColorF4 color = new(0f, 0f, 0f, 1f);
        int channels = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected a color channel name.");
            string name = reader.GetString()!;
            if (!reader.Read())
                throw new JsonException("Missing color channel value.");
            float value = JsonSerializer.Deserialize<float>(ref reader, options);
            if (!float.IsFinite(value))
                throw new JsonException("Color channels must be finite.");
            switch (name.ToUpperInvariant())
            {
                case "R": color.R = value; channels |= 1; break;
                case "G": color.G = value; channels |= 2; break;
                case "B": color.B = value; channels |= 4; break;
                case "A": color.A = value; break;
                default: throw new JsonException($"Unknown color channel '{name}'.");
            }
        }
        if (channels != 7)
            throw new JsonException("Color objects require R, G and B channels.");
        return color;
    }

    public override void Write(Utf8JsonWriter writer, ColorF4 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("R", value.R);
        writer.WriteNumber("G", value.G);
        writer.WriteNumber("B", value.B);
        writer.WriteNumber("A", value.A);
        writer.WriteEndObject();
    }
}

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Data.Colors;

namespace XREngine.Editor.Mcp;

/// <summary>Converts RGB colors without reflecting their unsafe pointer members.</summary>
internal sealed class McpColorF3JsonConverter : JsonConverter<ColorF3>
{
    private static readonly McpColorF4JsonConverter s_rgbaConverter = new();

    public override ColorF3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ColorF4 color = s_rgbaConverter.Read(ref reader, typeof(ColorF4), options);
        return new ColorF3(color.R, color.G, color.B);
    }

    public override void Write(Utf8JsonWriter writer, ColorF3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("R", value.R);
        writer.WriteNumber("G", value.G);
        writer.WriteNumber("B", value.B);
        writer.WriteEndObject();
    }
}

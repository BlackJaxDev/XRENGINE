using Newtonsoft.Json;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Tolerant converter for <see cref="ModelPostImportFlags"/>. Accepts strings (including
/// empty/whitespace as <see cref="ModelPostImportFlags.None"/>), integers, and null. Writes
/// the value as a comma-separated flag-name string so generated JSON stays human-readable.
/// </summary>
public sealed class ModelPostImportFlagsJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        Type targetType = Nullable.GetUnderlyingType(objectType) ?? objectType;
        return targetType == typeof(ModelPostImportFlags);
    }

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        bool nullable = Nullable.GetUnderlyingType(objectType) is not null;

        switch (reader.TokenType)
        {
            case JsonToken.Null:
                return nullable ? null : ModelPostImportFlags.None;
            case JsonToken.Integer when reader.Value is not null:
                return (ModelPostImportFlags)Convert.ToInt32(reader.Value);
            case JsonToken.String:
                string? raw = reader.Value as string;
                if (string.IsNullOrWhiteSpace(raw))
                    return ModelPostImportFlags.None;
                if (Enum.TryParse<ModelPostImportFlags>(raw, ignoreCase: true, out var parsed))
                    return parsed;
                return ModelPostImportFlags.None;
            default:
                return ModelPostImportFlags.None;
        }
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        var flags = (ModelPostImportFlags)value;
        writer.WriteValue(flags == ModelPostImportFlags.None ? nameof(ModelPostImportFlags.None) : flags.ToString());
    }
}

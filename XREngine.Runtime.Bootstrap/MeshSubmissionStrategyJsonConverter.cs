using Newtonsoft.Json;
using XREngine.Data.Rendering;

namespace XREngine.Runtime.Bootstrap;

public sealed class MeshSubmissionStrategyJsonConverter : JsonConverter
{
    private static bool _legacyGpuMeshletWarningLogged;

    public override bool CanConvert(Type objectType)
    {
        Type targetType = Nullable.GetUnderlyingType(objectType) ?? objectType;
        return targetType == typeof(EMeshSubmissionStrategy);
    }

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        bool nullable = Nullable.GetUnderlyingType(objectType) is not null;
        if (reader.TokenType == JsonToken.Null)
            return nullable ? null : default(EMeshSubmissionStrategy);

        if (reader.TokenType == JsonToken.String)
        {
            string? raw = reader.Value as string;
            if (EMeshSubmissionStrategyExtensions.TryParseMeshSubmissionStrategy(
                raw,
                out EMeshSubmissionStrategy strategy,
                out bool usedLegacyName))
            {
                LogLegacyGpuMeshletWarningOnce(usedLegacyName);
                return strategy;
            }
        }
        else if (reader.TokenType == JsonToken.Integer && reader.Value is not null)
        {
            EMeshSubmissionStrategy strategy = (EMeshSubmissionStrategy)Convert.ToInt32(reader.Value);
            if (Enum.IsDefined(strategy))
                return strategy;
        }

        throw new JsonSerializationException(
            $"Invalid {nameof(EMeshSubmissionStrategy)} value '{reader.Value}'.");
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteValue(value.ToString());
    }

    private static void LogLegacyGpuMeshletWarningOnce(bool usedLegacyName)
    {
        if (!usedLegacyName || _legacyGpuMeshletWarningLogged)
            return;

        _legacyGpuMeshletWarningLogged = true;
        Debug.Out("Legacy mesh submission strategy 'GpuMeshlet' was remapped to 'GpuMeshletZeroReadback'.");
    }
}

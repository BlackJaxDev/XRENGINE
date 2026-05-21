using Newtonsoft.Json;
using XREngine.Data.Rendering;

namespace XREngine.Runtime.Bootstrap;

public sealed class MeshSubmissionStrategyJsonConverter : JsonConverter<EMeshSubmissionStrategy>
{
    public override EMeshSubmissionStrategy ReadJson(
        JsonReader reader,
        Type objectType,
        EMeshSubmissionStrategy existingValue,
        bool hasExistingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.String)
        {
            string? raw = reader.Value as string;
            if (EMeshSubmissionStrategyExtensions.TryParseMeshSubmissionStrategy(
                raw,
                out EMeshSubmissionStrategy strategy,
                out _))
            {
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

    public override void WriteJson(JsonWriter writer, EMeshSubmissionStrategy value, JsonSerializer serializer)
        => writer.WriteValue(value.ToString());
}

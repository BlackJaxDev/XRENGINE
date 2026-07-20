using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace XREngine;

internal static class XREngineJsonRuntime
{
    public static void EnsureDynamicJsonRuntimeSupported(string operation, Type? payloadType = null)
    {
        if (!XRRuntimeEnvironment.IsPublishedBuild)
            return;

        string typeDetail = payloadType is null
            ? string.Empty
            : $" for '{payloadType.FullName}'";

        throw new NotSupportedException(
            $"Published runtime requires explicit System.Text.Json metadata{typeDetail} to {operation}. " +
            "Use a JsonTypeInfo/JsonSerializerContext overload or keep the payload at a raw JSON/JsonNode boundary.");
    }

    public static string SerializeWithTypeInfo(object payload, JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return JsonSerializer.Serialize(payload, typeInfo);
    }
}
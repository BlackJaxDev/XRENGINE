using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Networking;

/// <summary>
/// Runtime-neutral realtime join payload parsing and compatibility rules.
/// Application startup settings are deliberately mapped by the composition layer.
/// </summary>
public static class RealtimeJoinHandoffContract
{
    public const string PayloadEnvironmentVariable = "XRE_REALTIME_JOIN_PAYLOAD";
    public const string PayloadFileEnvironmentVariable = "XRE_REALTIME_JOIN_PAYLOAD_FILE";

    public static string CurrentProtocolVersion => RuntimeNetworkingHostServices.Current.ProtocolVersion;

    public static bool TryReadFromEnvironment(out RealtimeJoinHandoffPayload? payload, out string? source)
    {
        payload = null;
        source = null;

        string? payloadPath = GetOptionalEnvironmentValue(PayloadFileEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(payloadPath))
        {
            string resolvedPath = Path.GetFullPath(payloadPath);
            if (!File.Exists(resolvedPath))
                throw new FileNotFoundException("Realtime join handoff payload file was not found.", resolvedPath);

            payload = DeserializePayload(File.ReadAllText(resolvedPath), resolvedPath);
            source = $"{PayloadFileEnvironmentVariable}={resolvedPath}";
            return true;
        }

        string? payloadJson = GetOptionalEnvironmentValue(PayloadEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(payloadJson))
            return false;

        payload = DeserializePayload(payloadJson, PayloadEnvironmentVariable);
        source = PayloadEnvironmentVariable;
        return true;
    }

    public static bool IsProtocolCompatible(string? expectedProtocolVersion, string currentProtocolVersion)
    {
        if (string.IsNullOrWhiteSpace(expectedProtocolVersion) ||
            string.Equals(expectedProtocolVersion, "dev", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentProtocolVersion, "dev", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(expectedProtocolVersion.Trim(), currentProtocolVersion, StringComparison.OrdinalIgnoreCase);
    }

    public static string DescribeWorldAsset(WorldAssetIdentity? asset)
    {
        if (asset is null)
            return "<none>";

        string hash = WorldAssetIdentity.NormalizeHash(asset.ContentHash);
        if (hash.Length > 12)
            hash = hash[..12];
        if (string.IsNullOrWhiteSpace(hash))
            hash = "<empty>";

        return string.Create(CultureInfo.InvariantCulture, $"{asset.WorldId}@{asset.RevisionId}; hash={hash}; schema={asset.AssetSchemaVersion}");
    }

    private static RealtimeJoinHandoffPayload DeserializePayload(string json, string source)
    {
        try
        {
            return JsonSerializer.Deserialize(json, RealtimeJoinHandoffJsonContext.Default.RealtimeJoinHandoffPayload)
                ?? throw new InvalidOperationException("Realtime handoff payload was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Realtime handoff payload from {source} is not valid JSON.", ex);
        }
    }

    private static string? GetOptionalEnvironmentValue(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RealtimeJoinHandoffPayload))]
internal sealed partial class RealtimeJoinHandoffJsonContext : JsonSerializerContext
{
}

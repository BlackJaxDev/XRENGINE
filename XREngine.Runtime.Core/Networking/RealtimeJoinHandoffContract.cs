using System.Globalization;

namespace XREngine.Networking;

/// <summary>
/// Runtime-neutral realtime join compatibility and description rules.
/// Application startup settings are deliberately mapped by the composition layer.
/// </summary>
public static class RealtimeJoinHandoffContract
{
    public const string PayloadEnvironmentVariable = XREngineEnvironmentVariables.RealtimeJoinPayload;
    public const string PayloadFileEnvironmentVariable = XREngineEnvironmentVariables.RealtimeJoinPayloadFile;

    public static string CurrentProtocolVersion => RuntimeNetworkingHostServices.Current.ProtocolVersion;

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
}

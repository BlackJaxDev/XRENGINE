using System.Text.Json;
using XREngine.Networking;
using XREngine.Scene;

namespace XREngine.Browser;

public sealed partial class BrowserEngineAssetSource
{
    private WorldAssetIdentity? _verifiedWorldIdentity;
    private string? _verifiedNativeWorldPath;

    /// <summary>Consumes only the separate import populated after canonical and complete byte verification.</summary>
    private void ReadVerifiedWorldPackage(string json)
    {
        if (string.IsNullOrEmpty(json))
            return;
        using JsonDocument document = ParseVerifiedPackage(json);
        JsonElement package = document.RootElement;
        if (!string.Equals(RequirePackageString(RequirePackageObject(package, "metadata"), "browserStartupWorld"),
            StartupWorldPath, StringComparison.Ordinal))
            throw new InvalidDataException("AssetSource.WorldPackageInvalid: verified startup binding differs.");
        JsonElement asset = RequirePackageObject(package, "asset");
        Dictionary<string, string> metadata = new(StringComparer.Ordinal);
        foreach (JsonProperty entry in RequirePackageObject(asset, "metadata").EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String || !metadata.TryAdd(entry.Name, entry.Value.GetString()!))
                throw new InvalidDataException("AssetSource.WorldPackageInvalid: malformed verified asset metadata.");
        }
        if (!asset.TryGetProperty("assetSchemaVersion", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out int schemaVersion)
            || schemaVersion < 1)
            throw new InvalidDataException("AssetSource.WorldPackageInvalid: malformed verified asset schema.");
        _verifiedWorldIdentity = new WorldAssetIdentity
        {
            WorldId = RequirePackageString(asset, "worldId"),
            RevisionId = RequirePackageString(asset, "revisionId"),
            ContentHash = RequirePackageString(asset, "contentHash"),
            AssetSchemaVersion = schemaVersion,
            RequiredBuildVersion = RequirePackageString(asset, "requiredBuildVersion"),
            Metadata = metadata,
        };
        _verifiedNativeWorldPath = RequirePackageString(package, "worldEntryPoint");
    }

    private static JsonDocument ParseVerifiedPackage(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException error)
        {
            throw new InvalidDataException("AssetSource.WorldPackageInvalid: malformed verified package JSON.", error);
        }
    }

    private static JsonElement RequirePackageObject(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Object ? value
            : throw new InvalidDataException($"AssetSource.WorldPackageInvalid: missing verified object '{name}'.");

    private static string RequirePackageString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"AssetSource.WorldPackageInvalid: missing verified identity '{name}'.");
        return value.GetString()!;
    }

    /// <summary>Binds verified package identity to the exact hydrated startup world, never to handoff-supplied data.</summary>
    internal void RegisterVerifiedWorldIdentity(XRWorld world)
    {
        RequireSession();
        ArgumentNullException.ThrowIfNull(world);
        if (_verifiedWorldIdentity is null)
            return;
        if (!string.Equals(world.FilePath, StartupWorldPath, StringComparison.Ordinal)
            || world.GetType() != typeof(XRWorld) || _verifiedNativeWorldPath is null)
            throw new InvalidDataException("AssetSource.WorldPackageInvalid: the loaded world does not match its verified shared representation.");
        WorldAssetIdentityProvider.RegisterVerifiedIdentity(world, _verifiedWorldIdentity);
        // Replication carries the native manifest-relative spelling. Do not grant
        // aliases or package support files that have no replicated asset representation.
        WorldAssetIdentityProvider.RegisterVerifiedAssetPaths(world, [_verifiedNativeWorldPath]);
    }
}

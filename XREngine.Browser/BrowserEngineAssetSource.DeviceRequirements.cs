using System.Text.Json;
using XREngine.Core.Files;

namespace XREngine.Browser;

/// <summary>Reads and aggregates the device needs of packaged assets.</summary>
public sealed partial class BrowserEngineAssetSource
{
    private const string SupportedAssetLimit = "maxTextureArrayLayers";
    private const int CoreMaxTextureArrayLayers = 256;

    private readonly Dictionary<string, AssetDeviceRequirements> _assetDeviceRequirements = new(StringComparer.Ordinal);

    private void ReadAssetDeviceRequirements(string assetPath, JsonElement asset)
    {
        if (!asset.TryGetProperty("deviceRequirements", out JsonElement value))
            return;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
            !value.TryGetProperty("requiredFeatures", out JsonElement features) || features.ValueKind != JsonValueKind.Array ||
            features.GetArrayLength() != 0 ||
            !value.TryGetProperty("requiredLimits", out JsonElement limits) || limits.ValueKind != JsonValueKind.Object ||
            limits.EnumerateObject().Count() != 1)
            throw new InvalidDataException($"AssetSource.DeviceRequirementsInvalid: '{assetPath}'.");

        SortedDictionary<string, int> requiredLimits = new(StringComparer.Ordinal);
        foreach (JsonProperty limit in limits.EnumerateObject())
        {
            if (limit.Name != SupportedAssetLimit || limit.Value.ValueKind != JsonValueKind.Number ||
                !limit.Value.TryGetInt32(out int valueLimit) ||
                valueLimit <= CoreMaxTextureArrayLayers)
                throw new InvalidDataException($"AssetSource.DeviceRequirementsInvalid: '{assetPath}' has an unknown or invalid limit.");
            requiredLimits.Add(limit.Name, valueLimit);
        }

        _assetDeviceRequirements.Add(assetPath, new([], requiredLimits));
    }

    /// <summary>Returns deterministic requirements for startup and streamed asset closures.</summary>
    public string GetStartupDeviceRequirementsJson()
    {
        RequireSession();
        SortedSet<string> features = new(StringComparer.Ordinal);
        SortedDictionary<string, int> limits = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        Queue<string> pending = new();
        pending.Enqueue(StartupWorldPath);
        if (StartupSettingsPath is { } settingsPath)
            pending.Enqueue(settingsPath);
        foreach (string path in EssentialRoots)
            pending.Enqueue(path);
        foreach (string path in StreamedRoots)
            pending.Enqueue(path);
        while (pending.Count > 0)
        {
            string path = pending.Dequeue();
            if (!visited.Add(path) || !_assets.TryGetValue(path, out RuntimeAssetCatalogEntry? entry))
                continue;
            if (_assetDeviceRequirements.TryGetValue(path, out AssetDeviceRequirements? requirements))
            {
                features.UnionWith(requirements.RequiredFeatures);
                foreach ((string name, int value) in requirements.RequiredLimits)
                    limits[name] = Math.Max(limits.GetValueOrDefault(name), value);
            }
            foreach (string dependency in entry.Dependencies)
                pending.Enqueue(dependency);
        }

        return JsonSerializer.Serialize(new AssetDeviceRequirements([.. features], limits),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private sealed record AssetDeviceRequirements(string[] RequiredFeatures, SortedDictionary<string, int> RequiredLimits);
}

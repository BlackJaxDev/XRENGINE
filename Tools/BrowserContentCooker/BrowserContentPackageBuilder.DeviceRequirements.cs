using System.Text.Json;

namespace XREngine.Publishing;

public static partial class BrowserContentPackageBuilder
{
    private static readonly string[] DeviceRequirementLimits =
    [
        "maxTextureArrayLayers",
    ];

    private sealed record EngineAssetDeviceRequirements(string[] RequiredFeatures, SortedDictionary<string, int> RequiredLimits);

    /// <summary>Validates optional device requirements on one cooked asset entry.</summary>
    private static EngineAssetDeviceRequirements? ReadEngineAssetDeviceRequirements(JsonElement asset, string path)
    {
        if (!asset.TryGetProperty("deviceRequirements", out JsonElement value))
            return null;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2
            || !value.TryGetProperty("requiredFeatures", out JsonElement featureValues)
            || featureValues.ValueKind != JsonValueKind.Array || featureValues.GetArrayLength() != 0
            || !value.TryGetProperty("requiredLimits", out JsonElement limitValues)
            || limitValues.ValueKind != JsonValueKind.Object || limitValues.EnumerateObject().Count() != DeviceRequirementLimits.Length)
            throw new InvalidDataException($"Invalid device requirements for '{path}'.");

        SortedDictionary<string, int> limits = new(StringComparer.Ordinal);
        foreach (JsonProperty limit in limitValues.EnumerateObject())
        {
            if (!DeviceRequirementLimits.Contains(limit.Name, StringComparer.Ordinal)
                || limit.Value.ValueKind != JsonValueKind.Number || !limit.Value.TryGetInt32(out int minimum) || minimum <= 256)
                throw new InvalidDataException($"Invalid or unsupported device limit for '{path}'.");
            limits.Add(limit.Name, minimum);
        }
        if (limits.Count == 0)
            throw new InvalidDataException($"Empty device requirements are not allowed for '{path}'.");
        return new([], limits);
    }
}

using XREngine.Core.Files;
using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Wraps the cooked binary serializer with snapshot-specific filtering so play-mode
/// captures stay compact and never duplicate heavyweight asset data like meshes or textures.
/// </summary>
internal static class SnapshotBinarySerializer
{
    private static readonly CookedBinarySerializationCallbacks Callbacks = new()
    {
        OnSerializingValue = value => value switch
        {
            null => null,
            SnapshotAssetReference => value,
            XRAsset asset when ShouldInlineAsset(asset) => asset,
            XRAsset asset => SnapshotAssetReference.FromAsset(asset),
            _ => value
        },
        OnDeserializedValue = value => value is SnapshotAssetReference reference
            ? reference.Resolve() ?? value
            : value
    };

    public static byte[]? Serialize<T>(T instance)
    {
        if (instance is null)
            return null;

        return CookedBinarySerializer.Serialize(instance, Callbacks);
    }

    public static T? Deserialize<T>(byte[]? payload) where T : class
    {
        if (payload is null || payload.Length == 0)
            return null;

        return CookedBinarySerializer.Deserialize(typeof(T), payload, Callbacks) as T;
    }

    private static bool ShouldInlineAsset(XRAsset asset)
    {
        if (asset is XRWorld or XRScene or WorldSettings)
            return true;

        if (string.IsNullOrWhiteSpace(asset.FilePath))
            return true;

        return false;
    }
}

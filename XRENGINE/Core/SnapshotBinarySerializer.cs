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
            XRAsset asset => PrepareAssetForSnapshot(asset),
            _ => value
        },
        OnDeserializedValue = value => ResolveAssetReference(value)
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

    private static object? PrepareAssetForSnapshot(XRAsset asset)
    {
        if (ShouldInlineAsset(asset, out string reason))
        {
            SnapshotDiagnostics.LogAssetSerializationDecision(asset, SnapshotAssetSerializationMode.Inline, reason);
            return asset;
        }

        SnapshotDiagnostics.LogAssetSerializationDecision(asset, SnapshotAssetSerializationMode.Reference, reason);
        return SnapshotAssetReference.FromAsset(asset);
    }

    private static object? ResolveAssetReference(object? value)
    {
        if (value is not SnapshotAssetReference reference)
            return value;

        XRAsset? resolved = reference.Resolve();
        if (resolved is null)
            SnapshotDiagnostics.LogAssetResolveFailure(reference, "all reference lookup routes returned null");

        return resolved ?? value;
    }

    private static bool ShouldInlineAsset(XRAsset asset, out string reason)
    {
        if (asset is XRWorld or XRScene or WorldSettings)
        {
            reason = "snapshot root asset type";
            return true;
        }

        if (string.IsNullOrWhiteSpace(asset.FilePath))
        {
            reason = "asset has no file path";
            return true;
        }

        reason = "external asset uses cached/loaded asset reference";
        return false;
    }
}

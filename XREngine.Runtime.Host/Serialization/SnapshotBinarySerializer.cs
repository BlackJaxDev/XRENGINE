using XREngine.Data.Runtime.AotParity;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Wraps the cooked binary serializer with snapshot-specific filtering so play-mode
/// captures stay compact and never duplicate heavyweight asset data like meshes or textures.
/// File-backed assets are written as references to the loaded instance; every other asset is
/// written once per capture and shared by its later occurrences, so a restore keeps the
/// sharing of the captured graph (materials shared by many submeshes stay shared).
/// </summary>
internal static class SnapshotBinarySerializer
{
    private static readonly CookedBinarySerializationCallbacks Callbacks = new()
    {
        OnSerializingValue = value => value switch
        {
            null => null,
            SnapshotAssetReference or SnapshotTextFile => value,
            XRAsset asset => PrepareAssetForSnapshot(asset),
            _ => value
        },
        OnDeserializedValue = RestoreSnapshotValue,
        ShareReference = static value => value is XRAsset or SnapshotAssetReference or SnapshotTextFile
    };

    /// <summary>
    /// What each asset became in the capture in progress on this thread. The size and write
    /// passes and every occurrence of an asset then see the same reference or record, which the
    /// cooked format writes once and shares.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<XRAsset, object>? t_preparedAssets;

    /// <summary>
    /// Receives the render assets the read in progress on this thread creates, when its caller
    /// asked for them.
    /// </summary>
    [ThreadStatic]
    private static List<XRAsset>? t_createdRenderAssets;

    public static byte[]? Serialize<T>(T instance)
    {
        if (instance is null)
            return null;

        Dictionary<XRAsset, object>? outerPreparedAssets = t_preparedAssets;
        t_preparedAssets = new Dictionary<XRAsset, object>(ReferenceEqualityComparer.Instance);
        try
        {
            using var authoringScope = AotParityDiagnostics.EnterSynchronousAuthoringPath();
            return CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
                () => CookedBinarySerializer.Serialize(instance, Callbacks));
        }
        finally
        {
            t_preparedAssets = outerPreparedAssets;
        }
    }

    /// <param name="payload">A payload written by <see cref="Serialize{T}(T)"/>.</param>
    /// <param name="createdRenderAssets">
    /// Receives, in creation order, the assets the read creates that hold renderer resources:
    /// render objects, and meshes with the buffers they own. Assets resolved from references
    /// are loaded instances with other owners and are not included.
    /// </param>
    public static T? Deserialize<T>(byte[]? payload, List<XRAsset>? createdRenderAssets = null) where T : class
    {
        if (payload is null || payload.Length == 0)
            return null;

        List<XRAsset>? outerCreatedRenderAssets = t_createdRenderAssets;
        t_createdRenderAssets = createdRenderAssets;
        try
        {
            T? restored;
            using (AotParityDiagnostics.EnterSynchronousAuthoringPath())
                restored = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
                    () => CookedBinarySerializer.Deserialize(typeof(T), payload, Callbacks) as T);
            if (restored is XRScene scene)
                SnapshotSceneReferenceResolver.Repair(scene);
            return restored;
        }
        finally
        {
            t_createdRenderAssets = outerCreatedRenderAssets;
        }
    }

    private static object PrepareAssetForSnapshot(XRAsset asset)
    {
        Dictionary<XRAsset, object>? preparedAssets = t_preparedAssets;
        if (preparedAssets is not null && preparedAssets.TryGetValue(asset, out object? prepared))
            return prepared;

        prepared = DecideAssetForSnapshot(asset);
        preparedAssets?.Add(asset, prepared);
        return prepared;
    }

    private static object DecideAssetForSnapshot(XRAsset asset)
    {
        if (ShouldInlineAsset(asset, out string reason))
        {
            SnapshotDiagnostics.LogAssetSerializationDecision(asset, SnapshotAssetSerializationMode.Inline, reason);
            return asset is TextFile text && !string.IsNullOrWhiteSpace(text.FilePath)
                ? SnapshotTextFile.FromTextFile(text)
                : asset;
        }

        SnapshotDiagnostics.LogAssetSerializationDecision(asset, SnapshotAssetSerializationMode.Reference, reason);
        return SnapshotAssetReference.FromAsset(asset);
    }

    private static object? RestoreSnapshotValue(object? value)
    {
        switch (value)
        {
            case SnapshotAssetReference reference:
                return ResolveAssetReference(reference);
            case SnapshotTextFile text:
                return text.ToTextFile();
            case GenericRenderObject or XRMesh:
                // The reader created this asset: resolved assets arrive as references and
                // leave above as the loaded instance.
                t_createdRenderAssets?.Add((XRAsset)value);
                return value;
            default:
                return value;
        }
    }

    private static object ResolveAssetReference(SnapshotAssetReference reference)
    {
        XRAsset? resolved = reference.Resolve();
        if (resolved is null)
            SnapshotDiagnostics.LogAssetResolveFailure(reference, "all reference lookup routes returned null");

        return resolved ?? (object)reference;
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

        // A text file can claim a path while holding other text (a generated shader source
        // keeps its canonical file's path), and a reference resolves only to the asset
        // manager's own instance, so any other text file is written by value.
        if (asset is TextFile && !IsAssetManagerInstance(asset))
        {
            reason = "text file the asset manager does not hold";
            return true;
        }

        reason = "external asset uses cached/loaded asset reference";
        return false;
    }

    private static bool IsAssetManagerInstance(XRAsset asset)
    {
        if (asset.ID != Guid.Empty &&
            Engine.Assets.TryGetAssetByID(asset.ID, out XRAsset? byId) &&
            ReferenceEquals(byId, asset))
            return true;

        return asset.FilePath is { } path &&
            Engine.Assets.TryGetAssetByPath(path, out XRAsset? byPath) &&
            ReferenceEquals(byPath, asset);
    }
}

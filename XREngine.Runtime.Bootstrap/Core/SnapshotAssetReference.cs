using System;
using XREngine.Core.Files;
using XREngine.Diagnostics;

namespace XREngine;

/// <summary>
/// Lightweight handle written into world snapshots whenever a referenced asset
/// should be preserved by pointer instead of duplicating its full serialized payload.
/// </summary>
[Serializable]
internal sealed class SnapshotAssetReference
{
    public Guid AssetId { get; set; }
    public string? AssetPath { get; set; }
    public string? AssetType { get; set; }
    public string? AssetName { get; set; }

    public static SnapshotAssetReference FromAsset(XRAsset asset)
    {
        SnapshotAssetReference reference = new()
        {
            AssetId = asset.ID,
            AssetPath = asset.FilePath,
            AssetType = asset.GetType().AssemblyQualifiedName,
            AssetName = asset.Name
        };

        SnapshotDiagnostics.LogAssetReferenceCreated(reference, asset);
        return reference;
    }

    /// <summary>
    /// Finds the referenced asset: the loaded asset with its identity, else the one loaded at
    /// its path, else a fresh load. A loaded asset is accepted only when it is of the
    /// referenced type; different asset types can share a path (a shader and its source file).
    /// </summary>
    public XRAsset? Resolve()
    {
        SnapshotDiagnostics.LogAssetResolveStart(this);
        Type? targetType = ResolveAssetType();

        if (AssetId != Guid.Empty && Engine.Assets.GetAssetByID(AssetId) is XRAsset byId)
        {
            if (IsReferencedType(byId, targetType))
            {
                SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-id", byId);
                return byId;
            }

            SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-id", null, $"type mismatch: {byId.GetType().FullName}");
        }
        else if (AssetId != Guid.Empty)
            SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-id", null);

        if (!string.IsNullOrWhiteSpace(AssetPath)
            && Engine.Assets.TryGetAssetByPath(AssetPath, out XRAsset? byPath)
            && byPath is not null)
        {
            if (IsReferencedType(byPath, targetType))
            {
                SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-path", byPath);
                return byPath;
            }

            // Loading the path as the referenced type would evict the asset loaded there.
            SnapshotDiagnostics.LogAssetResolveFailure(this, $"the asset loaded at its path is a {byPath.GetType().FullName}");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(AssetPath))
            SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-path", null);

        if (string.IsNullOrWhiteSpace(AssetPath))
        {
            SnapshotDiagnostics.LogAssetResolveFailure(this, "reference has no asset path");
            return null;
        }

        if (targetType is null)
        {
            SnapshotDiagnostics.LogAssetResolveFailure(this, "asset type could not be resolved");
            return null;
        }

        return LoadAsset(targetType);
    }

    private static bool IsReferencedType(XRAsset asset, Type? targetType)
        => targetType is null || targetType.IsInstanceOfType(asset);

    private Type? ResolveAssetType()
    {
        if (string.IsNullOrWhiteSpace(AssetType))
            return null;

        try
        {
            return Type.GetType(AssetType, throwOnError: false, ignoreCase: false);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Snapshot asset reference failed to resolve type '{AssetType}': {ex.Message}");
            SnapshotDiagnostics.LogAssetResolveFailure(this, $"type resolution threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private XRAsset? LoadAsset(Type targetType)
    {
        foreach (string candidatePath in EnumerateCandidatePaths(AssetPath!))
        {
            try
            {
                if (Engine.Assets.Load(candidatePath, targetType) is XRAsset asset)
                {
                    SnapshotDiagnostics.LogAssetResolveAttempt(
                        this,
                        "load-from-path",
                        asset,
                        $"{targetType.FullName ?? targetType.Name} at '{candidatePath}'");
                    return asset;
                }

                if (Activator.CreateInstance(targetType) is XRAsset directAsset
                    && directAsset.Load3rdParty(candidatePath))
                {
                    directAsset.OriginalPath = candidatePath;
                    SnapshotDiagnostics.LogAssetResolveAttempt(
                        this,
                        "load-from-path-direct",
                        directAsset,
                        $"{targetType.FullName ?? targetType.Name} at '{candidatePath}'");
                    return directAsset;
                }

                SnapshotDiagnostics.LogAssetResolveAttempt(
                    this,
                    "load-from-path",
                    null,
                    $"loader returned non-asset for {targetType.FullName ?? targetType.Name} at '{candidatePath}'");
            }
            catch (Exception ex)
            {
                string displayName = string.IsNullOrEmpty(AssetName) ? AssetPath ?? AssetType ?? "unknown" : AssetName!;
                Debug.LogWarning($"Snapshot asset reference failed to load '{displayName}' from '{candidatePath}': {ex.Message}");
                SnapshotDiagnostics.LogAssetResolveFailure(this, $"load from '{candidatePath}' threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string assetPath)
    {
        if (Path.IsPathRooted(assetPath))
        {
            yield return assetPath;
            yield break;
        }

        string engineRelativePath = Path.Combine(Engine.Assets.EngineAssetsPath, assetPath);
        if (File.Exists(engineRelativePath))
            yield return engineRelativePath;

        string shaderRelativePath = Path.Combine(Engine.Assets.EngineAssetsPath, "Shaders", assetPath);
        if (File.Exists(shaderRelativePath))
            yield return shaderRelativePath;

        string gameRelativePath = Path.Combine(Engine.Assets.GameAssetsPath, assetPath);
        if (File.Exists(gameRelativePath))
            yield return gameRelativePath;
    }
}

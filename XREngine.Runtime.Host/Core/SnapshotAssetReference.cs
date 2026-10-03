using System;
using XREngine.Core.Files;
using XREngine.Data.Runtime.AotParity;
using XREngine.Diagnostics;
using XREngine.Serialization;

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

    public XRAsset? Resolve()
    {
        SnapshotDiagnostics.LogAssetResolveStart(this);

        Type? targetType = ResolveAssetType();
        if (targetType is null)
        {
            SnapshotDiagnostics.LogAssetResolveFailure(this, "asset type could not be resolved");
            return null;
        }

        if (AssetId != Guid.Empty && Engine.Assets.GetAssetByID(AssetId) is XRAsset byId
            && targetType.IsInstanceOfType(byId))
        {
            SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-id", byId);
            return byId;
        }

        if (AssetId != Guid.Empty)
            SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-id", null);

        if (!string.IsNullOrWhiteSpace(AssetPath)
            && Engine.Assets.TryGetAssetByPath(AssetPath, out XRAsset? byPath)
            && byPath is not null && targetType.IsInstanceOfType(byPath))
        {
            SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-path", byPath);
            return byPath;
        }

        if (!string.IsNullOrWhiteSpace(AssetPath))
            SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-path", null);

        if (string.IsNullOrWhiteSpace(AssetPath))
        {
            SnapshotDiagnostics.LogAssetResolveFailure(this, "reference has no asset path");
            return null;
        }

        return LoadAsset(targetType);
    }

    private Type? ResolveAssetType()
    {
        if (string.IsNullOrWhiteSpace(AssetType))
            return null;

        try
        {
            return AotRuntimeMetadataStore.ResolveType(AssetType);
        }
        catch (Exception ex) when (ex is not AotParityViolationException)
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
                // One source path can represent distinct assets, such as shader source text
                // and the shader that owns it. A path-cache hit must retain the saved type.
                XRAsset? asset = null;
                try
                {
                    asset = Engine.Assets.Load(candidatePath, targetType) as XRAsset;
                }
                catch (Exception ex) when (ex is not AotParityViolationException)
                {
                    SnapshotDiagnostics.LogAssetResolveFailure(this,
                        $"typed loader for '{candidatePath}' threw {ex.GetType().Name}: {ex.Message}");
                }

                if (asset is not null && targetType.IsInstanceOfType(asset))
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
                    directAsset.FilePath = candidatePath;
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
            catch (Exception ex) when (ex is not AotParityViolationException)
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

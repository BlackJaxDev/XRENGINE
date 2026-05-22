using System;
using System.Linq;
using System.Reflection;
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
    private static readonly MethodInfo LoadAssetGenericMethod = typeof(AssetManager)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .First(m => m.Name == nameof(AssetManager.Load)
                    && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(string));

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

        if (AssetId != Guid.Empty && Engine.Assets.GetAssetByID(AssetId) is XRAsset byId)
        {
            SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-id", byId);
            return byId;
        }

        if (AssetId != Guid.Empty)
            SnapshotDiagnostics.LogAssetResolveAttempt(this, "loaded-by-id", null);

        if (!string.IsNullOrWhiteSpace(AssetPath)
            && Engine.Assets.TryGetAssetByPath(AssetPath, out XRAsset? byPath)
            && byPath is not null)
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

        var targetType = ResolveAssetType();
        if (targetType is null)
        {
            SnapshotDiagnostics.LogAssetResolveFailure(this, "asset type could not be resolved");
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
        try
        {
            var method = LoadAssetGenericMethod.MakeGenericMethod(targetType);
            if (method.Invoke(Engine.Assets, new object[] { AssetPath! }) is XRAsset asset)
            {
                SnapshotDiagnostics.LogAssetResolveAttempt(this, "load-from-path", asset, targetType.FullName ?? targetType.Name);
                return asset;
            }

            SnapshotDiagnostics.LogAssetResolveAttempt(this, "load-from-path", null, $"loader returned non-asset for {targetType.FullName ?? targetType.Name}");
        }
        catch (Exception ex)
        {
            string displayName = string.IsNullOrEmpty(AssetName) ? AssetPath ?? AssetType ?? "unknown" : AssetName!;
            Debug.LogWarning($"Snapshot asset reference failed to load '{displayName}': {ex.Message}");
            SnapshotDiagnostics.LogAssetResolveFailure(this, $"load threw {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }
}

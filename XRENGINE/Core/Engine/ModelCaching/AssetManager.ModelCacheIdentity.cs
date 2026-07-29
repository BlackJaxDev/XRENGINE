using System.Reflection;
using XREngine.Core.Files.Caching;
using XREngine.Data.Core;
using XREngine.ModelCaching;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;

namespace XREngine;

public partial class AssetManager
{
    private bool TryResolveModelCachePath(
        string sourceFilePath,
        Type assetType,
        string? callerVariantKey,
        object? suppliedImportOptions,
        out string cachePath)
    {
        cachePath = string.Empty;
        if (!TryResolveModelCacheIdentity(
            sourceFilePath,
            assetType,
            callerVariantKey,
            suppliedImportOptions,
            out ModelCachePathResolution? resolution)
            || resolution is null)
            return false;

        cachePath = resolution.CachePath;
        return true;
    }

    internal bool TryResolveModelCacheIdentity(
        string sourceFilePath,
        Type assetType,
        string? callerVariantKey,
        object? suppliedImportOptions,
        out ModelCachePathResolution? resolution)
    {
        resolution = null;
        ArgumentNullException.ThrowIfNull(assetType);
        EnsureGameCachePathInitialized();
        if (string.IsNullOrWhiteSpace(GameCachePath))
            return false;

        try
        {
            string normalizedSourcePath = Path.GetFullPath(sourceFilePath);
            ModelImportOptions importOptions = suppliedImportOptions as ModelImportOptions
                ?? GetOrCreateThirdPartyImportOptions(
                    normalizedSourcePath,
                    assetType) as ModelImportOptions
                ?? new ModelImportOptions();
            importOptions.CookSettings ??= new ModelCookSettings();

            UnityModelImportProducerAdapter.EnsureRegistered();
            IRuntimeModelImportServices runtimeServices = RuntimeModelImportServices.Current;
            ModelImportBackendResolution backendResolution = ModelImportBackendResolver.Resolve(
                normalizedSourcePath,
                importOptions,
                runtimeServices.PreferredFbxBackend,
                runtimeServices.PreferredGltfBackend);

            if (backendResolution.Candidates.Count == 0)
                return false;
            if (!TryBuildModelCookOverrideSnapshot(
                normalizedSourcePath,
                importOptions.CookSettings,
                out ModelCookOverrideSnapshot cookOverrides))
                return false;

            ModelCacheSourceIdentity sourceIdentity = ModelCacheSourceIdentityResolver.Resolve(
                normalizedSourcePath,
                GameAssetsPath,
                EngineAssetsPath);
            ModelCacheVariantFingerprint fingerprint = ModelCacheVariantFingerprintBuilder.Compute(
                normalizedSourcePath,
                importOptions,
                backendResolution,
                cookOverrides,
                callerVariantKey,
                ResolveEngineBuildIdentity());
            resolution = ModelCachePathResolver.Resolve(
                GameCachePath!,
                sourceIdentity,
                backendResolution,
                fingerprint,
                AssetExtension);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.LogWarning(
                $"[ModelCache] Failed to resolve deterministic cache identity for '{sourceFilePath}'. {ex.Message}");
            return false;
        }
    }

    private bool TryBuildModelCookOverrideSnapshot(
        string sourceFilePath,
        ModelCookSettings modelDefaults,
        out ModelCookOverrideSnapshot snapshot)
    {
        snapshot = ModelCookOverrideSnapshot.Empty;
        if (!TryResolveGeneratedAssetPathForThirdPartySource(
            sourceFilePath,
            out string generatedAssetPath)
            || !File.Exists(generatedAssetPath))
            return true;

        string normalizedGeneratedPath = Path.GetFullPath(generatedAssetPath);
        try
        {
            XRPrefabSource? projectPrefab = LoadedAssetsByPathInternal.TryGetValue(
                normalizedGeneratedPath,
                out Core.Files.XRAsset? loadedAsset)
                ? loadedAsset as XRPrefabSource
                : null;

            if (projectPrefab is null)
            {
                using IDisposable suppression = XRObjectBase.SuppressObjectCacheRegistration();
                projectPrefab = DeserializeAssetFile<XRPrefabSource>(normalizedGeneratedPath);
            }

            if (projectPrefab is null)
                return false;

            snapshot = ModelCookOverrideSnapshotBuilder.Build(projectPrefab, modelDefaults);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.LogWarning(
                $"[ModelCache] Could not read authored cook overrides from '{normalizedGeneratedPath}'. " +
                $"Cache lookup is disabled for this import. {ex.Message}");
            return false;
        }
    }

    private static string ResolveEngineBuildIdentity()
    {
        Assembly assembly = typeof(AssetManager).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private void ProbeLegacyModelCache(
        string sourceFilePath,
        Type assetType,
        string? legacyVariantKey,
        string currentCachePath,
        DateTime sourceTimestampUtc)
    {
        if (!typeof(XRPrefabSource).IsAssignableFrom(assetType)
            || !IsSafeLegacyModelVariantKey(legacyVariantKey)
            || !TryResolveGenericCachePath(
                sourceFilePath,
                assetType,
                legacyVariantKey,
                out string legacyCachePath)
            || string.Equals(legacyCachePath, currentCachePath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(legacyCachePath))
            return;

        CacheReadResult result = ReadRegisteredCachePayload(
            legacyCachePath,
            sourceFilePath,
            sourceTimestampUtc,
            assetType,
            out _);
        LogRegisteredCacheReadDecision(legacyCachePath, assetType, result);
    }

    private static bool IsSafeLegacyModelVariantKey(string? legacyVariantKey)
    {
        if (string.IsNullOrWhiteSpace(legacyVariantKey))
            return true;

        return legacyVariantKey.Length <= 128
            && string.Equals(legacyVariantKey, legacyVariantKey.Trim(), StringComparison.Ordinal)
            && !legacyVariantKey.Equals(".", StringComparison.Ordinal)
            && !legacyVariantKey.Equals("..", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(legacyVariantKey), legacyVariantKey, StringComparison.Ordinal)
            && legacyVariantKey.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}

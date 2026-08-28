using System.Reflection;
using XREngine.Core.Files.Caching;

namespace XREngine.Rendering.Models.Caching;

/// <summary>ModelAssetPipeline-owned deterministic model cache identity policy.</summary>
internal sealed class ModelCachePathPolicy(
    Type modelAssetType,
    IModelCachePolicyServices services) : IThirdPartyCachePathPolicy
{
    private readonly Type _modelAssetType = modelAssetType ?? throw new ArgumentNullException(nameof(modelAssetType));
    private readonly IModelCachePolicyServices _services = services ?? throw new ArgumentNullException(nameof(services));

    public bool CanHandle(Type assetType)
        => _modelAssetType.IsAssignableFrom(assetType);

    public bool TryResolve(in ThirdPartyCachePathRequest request, out string cachePath)
    {
        cachePath = string.Empty;
        if (!TryResolveIdentity(request, out ModelCachePathResolution? resolution)
            || resolution is null)
        {
            return false;
        }

        cachePath = resolution.CachePath;
        return true;
    }

    internal bool TryResolveIdentity(
        in ThirdPartyCachePathRequest request,
        out ModelCachePathResolution? resolution)
    {
        resolution = null;
        if (!CanHandle(request.AssetType))
            return false;
        if (string.IsNullOrWhiteSpace(request.GameCachePath))
            return false;

        try
        {
            string sourcePath = Path.GetFullPath(request.SourceFilePath);
            ModelImportOptions importOptions = _services.GetImportOptions(
                sourcePath,
                request.AssetType,
                request.ImportOptions);
            importOptions.CookSettings ??= new ModelCookSettings();
            IRuntimeModelImportServices runtimeServices = RuntimeModelImportServices.Current;
            ModelImportBackendResolution backendResolution = ModelImportBackendResolver.Resolve(
                sourcePath,
                importOptions,
                runtimeServices.PreferredFbxBackend,
                runtimeServices.PreferredGltfBackend);
            if (backendResolution.Candidates.Count == 0
                || !_services.TryBuildCookOverrideSnapshot(
                    sourcePath,
                    importOptions.CookSettings,
                    out ModelCookOverrideSnapshot cookOverrides))
            {
                return false;
            }

            importOptions.CookOverrides = cookOverrides;
            ModelCacheSourceIdentity sourceIdentity = ModelCacheSourceIdentityResolver.Resolve(
                sourcePath,
                request.GameAssetsPath,
                request.EngineAssetsPath);
            ModelCacheVariantFingerprint fingerprint = ModelCacheVariantFingerprintBuilder.Compute(
                sourcePath,
                importOptions,
                backendResolution,
                cookOverrides,
                request.VariantKey,
                ResolveEngineBuildIdentity());
            resolution = ModelCachePathResolver.Resolve(
                request.GameCachePath!,
                sourceIdentity,
                backendResolution,
                fingerprint,
                request.AssetExtension);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.LogWarning(
                $"[ModelCache] Failed to resolve deterministic cache identity for " +
                $"'{request.SourceFilePath}'. {exception.Message}");
            return false;
        }
    }

    public void ProbeLegacy(
        in ThirdPartyCachePathRequest request,
        string currentCachePath,
        DateTime sourceTimestampUtc)
    {
        if (!IsSafeLegacyVariantKey(request.VariantKey)
            || !request.TryResolveGenericThirdPartyCachePath(
                request.SourceFilePath,
                request.AssetType,
                request.VariantKey,
                out string legacyCachePath)
            || string.Equals(legacyCachePath, currentCachePath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(legacyCachePath))
        {
            return;
        }

        IThirdPartyCacheCodec? codec = ThirdPartyCacheCodecRegistry.Find(request.AssetType);
        CacheReadResult result = codec?.Read(
            legacyCachePath,
            request.SourceFilePath,
            sourceTimestampUtc) ?? CacheReadResult.Miss();
        if (result.Status != CacheReadStatus.Hit)
        {
            Debug.Log(
                ELogCategory.Assets,
                "[ModelCache] Legacy cache probe {0} for '{1}'. reason={2}; detail={3}",
                result.Status,
                legacyCachePath,
                result.Reason,
                result.Detail ?? "none");
        }
    }

    private static string ResolveEngineBuildIdentity()
    {
        Assembly assembly = typeof(ModelCachePathPolicy).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static bool IsSafeLegacyVariantKey(string? variantKey)
    {
        if (string.IsNullOrWhiteSpace(variantKey))
            return true;

        return variantKey.Length <= 128
            && string.Equals(variantKey, variantKey.Trim(), StringComparison.Ordinal)
            && variantKey is not "." and not ".."
            && string.Equals(Path.GetFileName(variantKey), variantKey, StringComparison.Ordinal)
            && variantKey.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}

namespace XREngine;

/// <summary>Feature-owned cache identity policy invoked by the generic Runtime.Core cache.</summary>
public interface IThirdPartyCachePathPolicy
{
    bool CanHandle(Type assetType);

    bool TryResolve(in ThirdPartyCachePathRequest request, out string cachePath);

    void ProbeLegacy(in ThirdPartyCachePathRequest request, string currentCachePath, DateTime sourceTimestampUtc);
}

public delegate bool ThirdPartyCachePathResolver(
    string sourceFilePath,
    Type assetType,
    string? variantKey,
    out string cachePath);

/// <summary>
/// Engine-neutral inputs needed by a feature-owned cache path policy.
/// Runtime.Core supplies the required generic resolver callback without exposing
/// its mutable <see cref="AssetManager"/> instance across the feature boundary.
/// </summary>
public readonly record struct ThirdPartyCachePathRequest(
    string? GameCachePath,
    string? GameAssetsPath,
    string? EngineAssetsPath,
    string SourceFilePath,
    Type AssetType,
    string? VariantKey,
    object? ImportOptions,
    string AssetExtension,
    ThirdPartyCachePathResolver TryResolveGenericThirdPartyCachePath);

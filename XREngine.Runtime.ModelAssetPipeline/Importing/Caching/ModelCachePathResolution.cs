namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Complete deterministic model-cache path decision.
/// </summary>
public sealed class ModelCachePathResolution
{
    internal ModelCachePathResolution(
        string cachePath,
        bool usedHashedSourcePath,
        ModelCacheSourceIdentity sourceIdentity,
        string resolverKey,
        ModelCacheVariantFingerprint variantFingerprint)
    {
        CachePath = cachePath;
        UsedHashedSourcePath = usedHashedSourcePath;
        SourceIdentity = sourceIdentity;
        ResolverKey = resolverKey;
        VariantFingerprint = variantFingerprint;
    }

    public string CachePath { get; }
    public bool UsedHashedSourcePath { get; }
    public ModelCacheSourceIdentity SourceIdentity { get; }
    public string ResolverKey { get; }
    public ModelCacheVariantFingerprint VariantFingerprint { get; }
}

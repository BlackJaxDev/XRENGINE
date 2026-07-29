namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Culture-independent source identity used by model-cache path resolution.
/// </summary>
public sealed class ModelCacheSourceIdentity
{
    internal ModelCacheSourceIdentity(
        ModelCacheSourceOrigin origin,
        string canonicalIdentity,
        string canonicalAbsolutePath,
        string? rootRelativePath,
        string identityHash,
        bool usedFinalTargetFallback)
    {
        Origin = origin;
        CanonicalIdentity = canonicalIdentity;
        CanonicalAbsolutePath = canonicalAbsolutePath;
        RootRelativePath = rootRelativePath;
        IdentityHash = identityHash;
        UsedFinalTargetFallback = usedFinalTargetFallback;
    }

    public ModelCacheSourceOrigin Origin { get; }
    public string CanonicalIdentity { get; }
    public string CanonicalAbsolutePath { get; }
    public string? RootRelativePath { get; }
    public string IdentityHash { get; }
    public bool UsedFinalTargetFallback { get; }
}

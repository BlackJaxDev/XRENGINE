namespace XREngine.Rendering;

/// <summary>Metadata captured before resident decode and checked before the cache retains its pixels.</summary>
internal readonly record struct TextureResidentDataCacheKey(
    string AuthorityPath,
    long FileDiscoveryGeneration,
    long LastWriteUtcTicks,
    long Length,
    uint MaxResidentDimension,
    bool IncludeMipChain);

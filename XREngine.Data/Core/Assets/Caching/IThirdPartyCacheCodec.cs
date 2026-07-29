using System;

namespace XREngine.Core.Files.Caching
{
    /// <summary>
    /// Defines type ownership and typed read/write decisions for a third-party asset cache.
    /// </summary>
    public interface IThirdPartyCacheCodec
    {
        /// <summary>
        /// Gets the codec's ownership of the requested asset type.
        /// </summary>
        CacheCodecOwnership GetOwnership(Type assetType);

        /// <summary>
        /// Gets whether writes block the source-load path or run in the background.
        /// </summary>
        CacheWriteMode WriteMode { get; }

        /// <summary>
        /// Resolves the cache variant key used by this codec.
        /// </summary>
        string? ResolveDefaultVariantKey(string? explicitVariantKey);

        /// <summary>
        /// Produces the asset representation consumed by <see cref="Write"/>.
        /// </summary>
        XRAsset PrepareForWrite(string cachePath, XRAsset asset);

        /// <summary>
        /// Reads and validates a cache payload.
        /// </summary>
        CacheReadResult Read(string cachePath, string originalPath, DateTime sourceTimestampUtc);

        /// <summary>
        /// Writes a cache payload.
        /// </summary>
        CacheWriteResult Write(string cachePath, XRAsset cacheAsset, XRAsset originalAsset);
    }
}

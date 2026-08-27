using System;

namespace XREngine.Core.Files.Caching
{
    /// <summary>
    /// Defines type ownership and typed read/write decisions for a third-party asset cache.
    /// </summary>
    public interface IThirdPartyCacheCodec
    {
        /// <summary>
        /// Gets an optional stable capability name used by Runtime.Core entry points that cannot
        /// reference the feature-owned asset type directly.
        /// </summary>
        string? AuthorityRole => null;

        /// <summary>Gets the primary asset type associated with <see cref="AuthorityRole"/>.</summary>
        Type? AuthorityAssetType => null;

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

        /// <summary>Returns whether an existing payload is structurally usable before timestamp checks.</summary>
        bool IsCacheUsable(string cachePath)
            => true;

        /// <summary>Returns whether a cache hit lacks data that requires a source reimport.</summary>
        bool IsIncomplete(XRAsset asset, string sourceExtension)
            => false;

        /// <summary>
        /// Attempts to read a feature-owned binary asset file before the generic YAML path.
        /// Returning <see langword="true"/> claims the file even when
        /// <paramref name="asset"/> is <see langword="null"/>, allowing a codec
        /// to reject an incompatible binary payload without routing it through an
        /// unrelated text deserializer.
        /// </summary>
        bool TryReadDirectAssetFile(string filePath, Type assetType, out XRAsset? asset)
        {
            asset = null;
            return false;
        }
    }
}

namespace XREngine.Core.Files.Caching
{
    /// <summary>
    /// Describes whether a cache codec participates in, or exclusively owns, an asset type.
    /// </summary>
    public enum CacheCodecOwnership
    {
        /// <summary>
        /// The codec does not handle the requested asset type.
        /// </summary>
        NotHandled,

        /// <summary>
        /// The codec may handle its own payload, but a cache miss may fall through to the
        /// generic asset-cache representation.
        /// </summary>
        Cooperative,

        /// <summary>
        /// The codec owns cache reads and writes for the asset type. Generic cache
        /// deserialization and serialization must never run after this codec is selected.
        /// </summary>
        Exclusive,
    }
}

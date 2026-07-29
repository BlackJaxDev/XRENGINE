namespace XREngine.Core.Files.Caching
{
    /// <summary>
    /// Describes the outcome of a third-party cache write.
    /// </summary>
    public enum CacheWriteStatus
    {
        /// <summary>
        /// The codec intentionally did not write a payload.
        /// </summary>
        Skipped = 0,

        /// <summary>
        /// The cache payload was written successfully.
        /// </summary>
        Written,

        /// <summary>
        /// The codec attempted to write a payload but failed.
        /// </summary>
        Failed,
    }
}

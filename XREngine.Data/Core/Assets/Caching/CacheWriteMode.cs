namespace XREngine.Core.Files.Caching
{
    /// <summary>
    /// Controls whether a codec write completes before the source asset load returns.
    /// </summary>
    public enum CacheWriteMode
    {
        /// <summary>
        /// Complete the cache write on the current cache-write path.
        /// </summary>
        Blocking,

        /// <summary>
        /// Schedule the cache write in the background.
        /// </summary>
        Background,
    }
}

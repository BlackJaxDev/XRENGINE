namespace XREngine.Core.Files.Caching
{
    /// <summary>
    /// Describes the outcome of a third-party cache read.
    /// </summary>
    public enum CacheReadStatus
    {
        /// <summary>
        /// The codec did not find a payload that it could read.
        /// </summary>
        Miss = 0,

        /// <summary>
        /// A compatible cached asset was read successfully.
        /// </summary>
        Hit,

        /// <summary>
        /// A payload was found but rejected for a specific compatibility or safety reason.
        /// </summary>
        Rejected,
    }
}

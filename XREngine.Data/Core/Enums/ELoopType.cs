namespace XREngine
{
    /// <summary>
    /// Specifies how loops should be executed.
    /// </summary>
    public enum ELoopType
    {
        /// <summary>Execute items sequentially.</summary>
        Sequential,
        /// <summary>Execute items asynchronously.</summary>
        Asynchronous,
        /// <summary>Execute items in parallel.</summary>
        Parallel
    }
}

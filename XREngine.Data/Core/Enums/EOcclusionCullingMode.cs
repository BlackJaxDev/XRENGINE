namespace XREngine
{
    /// <summary>
    /// Selects which occlusion culling path is active for GPU indirect rendering.
    /// </summary>
    public enum EOcclusionCullingMode
    {
        /// <summary>
        /// Occlusion culling is disabled.
        /// </summary>
        Disabled = 0,

        /// <summary>
        /// GPU Hi-Z occlusion path.
        /// </summary>
        GpuHiZ = 1,

        /// <summary>
        /// CPU-compatible asynchronous query path.
        /// </summary>
        CpuQueryAsync = 2
    }
}

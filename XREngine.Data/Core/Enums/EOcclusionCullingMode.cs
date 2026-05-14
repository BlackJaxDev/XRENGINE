namespace XREngine
{
    /// <summary>
    /// Selects which mesh occlusion culling path is active.
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
        CpuQueryAsync = 2,

        /// <summary>
        /// CPU software rasterizer occlusion path.
        /// </summary>
        CpuSoftwareOcclusion = 3
    }
}

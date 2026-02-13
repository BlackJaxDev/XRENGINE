namespace XREngine
{
    /// <summary>
    /// Selects which command-data layout policy to use for GPU culling preparation.
    /// </summary>
    public enum EGpuCullingDataLayout
    {
        /// <summary>
        /// Auto-select based on command count and available shader path support.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Use the default AoS + hot-command split path.
        /// </summary>
        AoSHot = 1,

        /// <summary>
        /// Enable SoA extraction path for culling-prep experimentation/benchmarking.
        /// </summary>
        SoA = 2,
    }
}

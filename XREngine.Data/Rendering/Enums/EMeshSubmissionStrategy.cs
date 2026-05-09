namespace XREngine.Data.Rendering
{
    /// <summary>
    /// Declares the CPU/GPU mesh submission contract for render passes.
    /// </summary>
    public enum EMeshSubmissionStrategy
    {
        /// <summary>
        /// Traverse and draw mesh commands directly on the CPU.
        /// </summary>
        CpuDirect = 0,

        /// <summary>
        /// Use the GPU indirect path with diagnostic readbacks and explicit fallback support.
        /// </summary>
        GpuIndirectInstrumented = 1,

        /// <summary>
        /// Use the production GPU indirect path without hot-path CPU readbacks or CPU fallbacks.
        /// </summary>
        GpuIndirectZeroReadback = 2,

        /// <summary>
        /// Use the meshlet/task-mesh submission path when the active renderer supports it.
        /// </summary>
        GpuMeshlet = 3,
    }
}

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
        /// Use the production meshlet/task-mesh submission path without hot-path CPU readbacks.
        /// </summary>
        GpuMeshletZeroReadback = 3,

        /// <summary>
        /// Use the diagnostic meshlet/task-mesh submission path. CPU readback, timestamp queries,
        /// and per-batch logging are permitted when diagnostics are explicitly enabled.
        /// </summary>
        GpuMeshletInstrumented = 4,
    }
}

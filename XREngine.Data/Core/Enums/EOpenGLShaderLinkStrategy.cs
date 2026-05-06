namespace XREngine
{
    /// <summary>
    /// Selects how uncached OpenGL shader programs are compiled and linked.
    /// </summary>
    public enum EOpenGLShaderLinkStrategy
    {
        /// <summary>
        /// Prefer driver-parallel compile/link when the startup probe passes, otherwise
        /// use the shared-context source queue when available. Known hazard shapes
        /// bypass async source lanes and use the guarded synchronous fallback.
        /// </summary>
        Auto,

        /// <summary>
        /// Compile and link non-hazard programs on the shared OpenGL context thread.
        /// Known hazard shapes are rejected by the queue and use the guarded
        /// synchronous fallback instead.
        /// </summary>
        SharedContext,

        /// <summary>
        /// Use GL_ARB_parallel_shader_compile or GL_KHR_parallel_shader_compile on the
        /// render context when the startup probe passes. If unavailable, the engine
        /// falls back to the shared-context source queue and then synchronous linking.
        /// </summary>
        DriverParallel,

        /// <summary>
        /// Compile and link source programs synchronously on the render thread.
        /// Binary cache uploads still follow AsyncProgramBinaryUpload; turn that
        /// setting off when diagnosing fully synchronous cache loads.
        /// </summary>
        Synchronous,
    }
}

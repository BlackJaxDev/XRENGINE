namespace XREngine
{
    /// <summary>
    /// Selects how uncached OpenGL shader programs are compiled and linked.
    /// </summary>
    public enum EOpenGLShaderLinkStrategy
    {
        /// <summary>
        /// Prefer the shared-context compile/link queue and fall back to synchronous linking
        /// when no async queue exists.
        /// </summary>
        Auto,

        /// <summary>
        /// Compile and link programs on the shared OpenGL context thread.
        /// </summary>
        SharedContext,

        /// <summary>
        /// Use GL_ARB_parallel_shader_compile or GL_KHR_parallel_shader_compile on the render context.
        /// </summary>
        DriverParallel,

        /// <summary>
        /// Compile and link programs synchronously on the render thread.
        /// This is useful for diagnosis only and can stall startup.
        /// </summary>
        Synchronous,
    }
}

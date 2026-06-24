namespace XREngine
{
    /// <summary>
    /// Controls whether startup may fall back to another rendering backend when the requested backend fails.
    /// </summary>
    public enum RenderBackendFallbackPolicy
    {
        /// <summary>
        /// The requested backend is required. Startup fails visibly when it cannot initialize.
        /// </summary>
        RequireRequested,

        /// <summary>
        /// Startup may fall back to OpenGL after logging the failed requested backend and exception summary.
        /// </summary>
        FallbackWithWarning,

        /// <summary>
        /// Startup treats the requested backend as preferred and may choose a compatible fallback with diagnostics.
        /// </summary>
        AutoPreferRequested,
    }
}

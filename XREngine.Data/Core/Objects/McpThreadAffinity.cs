namespace XREngine.Data.Core
{
    /// <summary>
    /// Specifies which engine thread an MCP tool should execute on.
    /// Applied via <see cref="McpThreadAffinityAttribute"/> or resolved from a global default.
    /// </summary>
    public enum McpThreadAffinity
    {
        /// <summary>
        /// Run on the calling thread (HTTP listener threadpool thread).
        /// Fastest for read-only queries that don't touch thread-sensitive state.
        /// </summary>
        Caller = 0,

        /// <summary>
        /// Dispatch to the main (render) thread.
        /// Required for GPU resource operations, OpenGL/Vulkan calls, and scene graph mutations
        /// that flow through the render pipeline.
        /// </summary>
        Main = 1,

        /// <summary>
        /// Dispatch to the engine update thread.
        /// Suitable for game logic, play-mode transitions, and non-render scene mutations.
        /// </summary>
        Update = 2,

        /// <summary>
        /// Dispatch to the physics thread.
        /// Required for PhysX scene mutations (add/remove/release bodies).
        /// </summary>
        Physics = 3,

        /// <summary>
        /// Schedule on a job worker thread via the <see cref="JobManager"/>.
        /// Useful for CPU-heavy operations that should not block the HTTP listener.
        /// </summary>
        JobWorker = 4,
    }
}

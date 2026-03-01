namespace XREngine.Data.Core
{
    /// <summary>
    /// Controls how MCP tool invocations are dispatched by default when a tool
    /// does not declare an explicit <see cref="McpThreadAffinityAttribute"/>.
    /// Configurable in Global Editor Preferences → MCP Server.
    /// </summary>
    public enum McpDispatchMode
    {
        /// <summary>
        /// Execute tools directly on the HTTP listener threadpool thread.
        /// Lowest latency, but tools must not touch thread-sensitive engine state.
        /// </summary>
        Direct = 0,

        /// <summary>
        /// Dispatch tools to the main (render) thread via the job manager.
        /// Safest default — guarantees access to scene graph, GPU resources, and ImGui state.
        /// </summary>
        MainThread = 1,

        /// <summary>
        /// Schedule tools on a job worker thread via the job manager.
        /// Good for CPU-heavy tools that should not block the HTTP listener or render thread.
        /// </summary>
        JobWorker = 2,
    }
}

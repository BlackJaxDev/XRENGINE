using XREngine.Rendering;

namespace XREngine.Editor.Mcp
{
    /// <summary>
    /// Provides execution context for MCP tool invocations.
    /// </summary>
    public sealed class McpToolContext
    {
        /// <summary>
        /// Creates a new tool context.
        /// </summary>
        /// <param name="worldInstance">The active world instance for the tool to operate on.</param>
        public McpToolContext(XRWorldInstance worldInstance)
        {
            WorldInstance = worldInstance;
        }

        /// <summary>
        /// The active world instance that the tool should operate on.
        /// </summary>
        public XRWorldInstance WorldInstance { get; }
    }
}

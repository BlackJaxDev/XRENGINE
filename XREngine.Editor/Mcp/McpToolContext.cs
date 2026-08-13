using XREngine.Rendering;
using McpCapability = XREngine.Runtime.Automation.Mcp.McpCapability;

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
        /// <param name="worldInstance">The optional active world instance for the tool to operate on.</param>
        public McpToolContext(XRWorldInstance? worldInstance)
        {
            WorldInstanceOrNull = worldInstance;
            Capabilities = McpCapability.ProfilerSession;
            if (worldInstance is not null)
                Capabilities |= McpCapability.World | McpCapability.Renderer | McpCapability.RenderTarget;
        }

        /// <summary>
        /// The active world instance that the tool should operate on.
        /// </summary>
        public XRWorldInstance WorldInstance
            => WorldInstanceOrNull ?? throw new InvalidOperationException("This MCP tool requires an active world instance.");

        public XRWorldInstance? WorldInstanceOrNull { get; }

        public McpCapability Capabilities { get; }
    }
}

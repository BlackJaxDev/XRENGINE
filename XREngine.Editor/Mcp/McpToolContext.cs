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
        /// <param name="world">The optional active Core world for the tool to operate on.</param>
        public McpToolContext(RuntimeWorld? world)
        {
            WorldOrNull = world;
            Capabilities = McpCapability.ProfilerSession;
            if (world is not null)
                Capabilities |= McpCapability.World;
            if (world?.TryGetCapability<IRuntimeRenderWorld>(out _) == true)
                Capabilities |= McpCapability.Renderer | McpCapability.RenderTarget;
        }

        /// <summary>
        /// The active Core world that the tool should operate on.
        /// </summary>
        public RuntimeWorld World
            => WorldOrNull ?? throw new InvalidOperationException("This MCP tool requires an active runtime world.");

        public RuntimeWorld? WorldOrNull { get; }

        /// <summary>Resolves the rendering capability only for tools that require it.</summary>
        public IRuntimeRenderWorld RenderWorld
            => World.TryGetCapability<IRuntimeRenderWorld>(out IRuntimeRenderWorld? renderWorld)
                && renderWorld is not null
                ? renderWorld
                : throw new InvalidOperationException("This MCP tool requires a rendering capability attached to the active runtime world.");

        /// <summary>Resolves editor-only scene policy explicitly rather than through the Core world.</summary>
        public EditorWorldIntegration EditorWorld
            => EditorWorldIntegrationRegistry.TryGet(World, out EditorWorldIntegration? editorWorld)
                && editorWorld is not null
                ? editorWorld
                : throw new InvalidOperationException("This MCP tool requires editor-world integration for the active runtime world.");

        public McpCapability Capabilities { get; }
    }
}

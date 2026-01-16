using XREngine.Rendering;

namespace XREngine.Editor.Mcp
{
    public sealed class McpToolContext
    {
        public McpToolContext(XRWorldInstance worldInstance)
        {
            WorldInstance = worldInstance;
        }

        public XRWorldInstance WorldInstance { get; }
    }
}

namespace XREngine.Runtime.Automation.Mcp;

/// <summary>Optional host services which an MCP tool may require.</summary>
[Flags]
public enum McpCapability
{
    None = 0,
    World = 1 << 0,
    Renderer = 1 << 1,
    RenderTarget = 1 << 2,
    ProfilerSession = 1 << 3,
    Editor = 1 << 4,
    Window = 1 << 5,
}

namespace XREngine.Runtime.Automation.Mcp;

/// <summary>A host-specific group of MCP tools registered with the runtime transport.</summary>
public interface IMcpToolBundle
{
    IEnumerable<McpToolDefinition> GetTools();
}

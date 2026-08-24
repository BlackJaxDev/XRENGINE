using McpCapability = XREngine.Runtime.Automation.Mcp.McpCapability;

namespace XREngine.Editor.Mcp;

/// <summary>Overrides the default world capability required by an editor MCP action.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpRequiredCapabilitiesAttribute(McpCapability capabilities) : Attribute
{
    public McpCapability Capabilities { get; } = capabilities;
}

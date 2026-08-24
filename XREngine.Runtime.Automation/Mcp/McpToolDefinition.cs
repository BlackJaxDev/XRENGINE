using System.Text.Json;

namespace XREngine.Runtime.Automation.Mcp;

/// <summary>Metadata and invocation delegate for one runtime MCP tool.</summary>
public sealed record McpToolDefinition(
    string Name,
    string Description,
    object InputSchema,
    Func<McpToolContext, JsonElement, CancellationToken, Task<McpToolResponse>> Handler,
    McpCapability RequiredCapabilities = McpCapability.None,
    McpPermissionLevel Permission = McpPermissionLevel.ReadOnly);

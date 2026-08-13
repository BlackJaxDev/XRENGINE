using IMcpToolBundle = XREngine.Runtime.Automation.Mcp.IMcpToolBundle;
using EditorPermissionLevel = XREngine.Data.Core.McpPermissionLevel;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Registers the editor-owned scene/action catalog with a runtime MCP registry without making
/// runtime profiler tools reference the editor assembly.
/// </summary>
public sealed class EditorMcpToolBundle(Func<McpToolContext> contextFactory) : IMcpToolBundle
{
    public IEnumerable<XREngine.Runtime.Automation.Mcp.McpToolDefinition> GetTools()
    {
        foreach (McpToolDefinition editorTool in McpToolRegistry.Tools)
        {
            yield return new XREngine.Runtime.Automation.Mcp.McpToolDefinition(
                editorTool.Name,
                editorTool.Description,
                editorTool.InputSchema,
                async (_, arguments, cancellationToken) =>
                {
                    McpToolResponse response = await editorTool.Handler(
                        contextFactory(), arguments, cancellationToken).ConfigureAwait(false);
                    return new XREngine.Runtime.Automation.Mcp.McpToolResponse(
                        response.Message, response.Data, response.IsError);
                },
                editorTool.RequiredCapabilities,
                ConvertPermission(editorTool.PermissionLevel));
        }
    }

    private static XREngine.Runtime.Automation.Mcp.McpPermissionLevel ConvertPermission(EditorPermissionLevel permission)
        => permission switch
        {
            EditorPermissionLevel.ReadOnly => XREngine.Runtime.Automation.Mcp.McpPermissionLevel.ReadOnly,
            EditorPermissionLevel.Destructive or EditorPermissionLevel.Arbitrary
                => XREngine.Runtime.Automation.Mcp.McpPermissionLevel.Destructive,
            _ => XREngine.Runtime.Automation.Mcp.McpPermissionLevel.Mutating,
        };
}

using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    [XRMcp(Name = "load_game_project", Permission = McpPermissionLevel.Destructive, PermissionReason = "Replaces the editor's active authoring project and its settings.")]
    [Description("Load a local .xrproj authoring project in edit mode. Compile its scripts separately before loading worlds that use game types.")]
    public static Task<McpToolResponse> LoadGameProjectAsync([McpName("project_path")] string projectPath)
    {
        if (!EditorState.InEditMode)
            return Task.FromResult(new McpToolResponse("Exit play mode before loading a project.", isError: true));
        string fullPath = Path.GetFullPath(projectPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".xrproj", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath) || !Engine.LoadProject(fullPath))
            return Task.FromResult(new McpToolResponse("The authoring project could not be loaded.", isError: true));
        return Task.FromResult(new McpToolResponse("Loaded authoring project.", new { project = Engine.CurrentProject?.ProjectName }));
    }
}

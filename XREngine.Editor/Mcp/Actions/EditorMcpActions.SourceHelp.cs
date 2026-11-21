using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Editor.Services.AssistantHelp;
using McpCapability = XREngine.Runtime.Automation.Mcp.McpCapability;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>Finds literal matches in bounded engine source and documentation files.</summary>
    [XRMcp(Name = "search_engine_help", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.JobWorker)]
    [McpRequiredCapabilities(McpCapability.None)]
    [Description("Search engine C# source and Markdown guides for literal text. Results are text matches, not semantic symbol references.")]
    public static Task<McpToolResponse> SearchEngineHelpAsync(
        McpToolContext context,
        [McpName("query"), Description("Literal term to find, 1 to 128 characters.")] string query,
        [McpName("scope"), Description("Search all, source, or docs.")] string scope = "all",
        [McpName("max_results"), Description("Maximum matches to return, 1 to 20.")] int maxResults = 12,
        CancellationToken token = default)
        => EngineSourceHelpService.SearchAsync(query, scope, maxResults, token);

    /// <summary>Reads a bounded file range and returns its source and hash evidence.</summary>
    [XRMcp(Name = "read_engine_help", Permission = McpPermissionLevel.ReadOnly)]
    [McpThreadAffinity(McpThreadAffinity.JobWorker)]
    [McpRequiredCapabilities(McpCapability.None)]
    [Description("Read numbered lines from one engine source or guide file returned by search_engine_help.")]
    public static Task<McpToolResponse> ReadEngineHelpAsync(
        McpToolContext context,
        [McpName("path"), Description("Repository-relative path from search_engine_help.")] string path,
        [McpName("start_line"), Description("First line to read, starting at 1.")] int startLine = 1,
        [McpName("line_count"), Description("Number of lines to read, 1 to 120.")] int lineCount = 80,
        CancellationToken token = default)
        => EngineSourceHelpService.ReadAsync(path, startLine, lineCount, token);
}

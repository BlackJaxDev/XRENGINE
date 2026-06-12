namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private sealed class ToolCallEntry
    {
        public required string ToolName { get; init; }
        public string ArgsSummary { get; init; } = string.Empty;
        public string ResultSummary { get; set; } = string.Empty;
        /// <summary>
        /// A compact summary of the tool result that preserves IDs (node IDs, asset IDs, etc.).
        /// Used by <see cref="BuildConversationContextBlock"/> to keep ID references available
        /// across continuation turns so the model doesn't need to re-query.
        /// </summary>
        public string ContextResultSummary { get; set; } = string.Empty;
        public bool IsError { get; set; }
        public bool IsComplete { get; set; }
        /// <summary>Local file path returned by the tool (e.g. screenshot path), for UI rendering.</summary>
        public string? ResultFilePath { get; set; }
    }
}

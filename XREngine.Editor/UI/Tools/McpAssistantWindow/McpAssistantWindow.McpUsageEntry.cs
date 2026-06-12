namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private sealed class McpUsageEntry
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string Provider { get; init; } = string.Empty;
        public string PromptPreview { get; init; } = string.Empty;
        public bool AttachRequested { get; init; }
        public bool McpPayloadIncluded { get; set; }
        public string ToolChoice { get; set; } = "none";
        public bool RequireToolUse { get; set; }
        public bool McpDiscoveryFailed { get; set; }
        public bool RetriedWithoutMcp { get; set; }
        public int McpEventCount { get; set; }
        public int ToolEventCount { get; set; }
        public string Result { get; set; } = "Pending";
        public string Note { get; set; } = string.Empty;
    }
}

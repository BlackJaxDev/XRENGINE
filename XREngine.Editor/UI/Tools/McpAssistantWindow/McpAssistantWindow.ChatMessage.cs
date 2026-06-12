namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    private sealed class ChatMessage
    {
        public required string Role { get; init; }
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public bool IsStreaming { get; set; }
        public List<ToolCallEntry> ToolCalls { get; } = [];
        public object ToolCallsSyncRoot { get; } = new();

        /// <summary>
        /// Ordered list of content segments for chronological rendering.
        /// Populated during streaming. If empty, falls back to Content + ToolCalls.
        /// </summary>
        public List<ContentSegment> Segments { get; } = [];
        public object SegmentsSyncRoot { get; } = new();
    }
}

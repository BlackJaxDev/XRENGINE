namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    /// <summary>
    /// Represents a chronological segment of assistant output — either a block
    /// of text or a group of tool calls that occurred at that point in time.
    /// </summary>
    private sealed class ContentSegment
    {
        public enum SegmentKind { Text, ToolCallGroup }
        public int UiId { get; init; }
        public SegmentKind Kind { get; init; }
        /// <summary>Text content (for Text segments).</summary>
        public string Text { get; set; } = string.Empty;
        /// <summary>Tool call entries (for ToolCallGroup segments).</summary>
        public List<ToolCallEntry> ToolCalls { get; } = [];
    }
}

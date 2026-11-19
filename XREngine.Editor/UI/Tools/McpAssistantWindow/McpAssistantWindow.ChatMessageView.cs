using System.Globalization;

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    /// <summary>Retains display data until the message or its tool list changes.</summary>
    private sealed class ChatMessageView(ChatMessage message)
    {
        public string Timestamp { get; } = message.Timestamp.ToString("g", CultureInfo.CurrentCulture);
        public ContentSegment[] Segments { get; set; } = [];
        public ToolCallEntry[] Tools { get; set; } = [];
        public Dictionary<int, ChatActivityView> Activities { get; } = [];
    }
}

namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    /// <summary>Caches a tool group label and membership between execution events.</summary>
    private sealed class ChatActivityView
    {
        public ToolCallEntry[] Tools { get; set; } = [];
        public int Completed { get; set; } = -1;
        public int Errors { get; set; } = -1;
        public int Total { get; set; } = -1;
        public bool Running { get; set; }
        public string Label { get; set; } = string.Empty;
    }
}

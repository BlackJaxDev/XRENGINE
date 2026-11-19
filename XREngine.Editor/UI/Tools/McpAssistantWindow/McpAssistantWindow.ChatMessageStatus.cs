namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    /// <summary>
    /// States what the editor observed for an assistant reply. It does not claim
    /// that a scene change was verified or can be undone.
    /// </summary>
    private enum ChatMessageStatus
    {
        Running,
        Completed,
        Incomplete,
        Stopped,
        Failed,
    }
}

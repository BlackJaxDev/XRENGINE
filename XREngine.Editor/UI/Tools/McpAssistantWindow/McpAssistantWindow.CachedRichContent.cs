namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    /// <summary>Keeps parsed lines until a message segment changes its source text.</summary>
    private sealed record CachedRichContent(string Source, RichLine[] Lines);
}

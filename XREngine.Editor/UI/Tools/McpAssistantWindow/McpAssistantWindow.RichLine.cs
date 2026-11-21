namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    /// <summary>Stores the text and inline pieces needed to draw one response line.</summary>
    private sealed record RichLine(RichLineKind Kind, string Text, float Indent, RichPiece[] Pieces);
}

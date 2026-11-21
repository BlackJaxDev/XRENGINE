namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    /// <summary>Stores one parsed text style or scene-object link for a chat line.</summary>
    private sealed class RichPiece(RichPieceKind kind, string text)
    {
        private string? _buttonDisplayName;
        private string? _buttonLabel;

        public RichPieceKind Kind { get; } = kind;
        public string Text { get; } = text;
        public Guid? ParsedGuid { get; } = kind == RichPieceKind.Guid && Guid.TryParse(text, out Guid guid) ? guid : null;
        public string MissingObjectLabel { get; } = kind == RichPieceKind.Guid ? text[..Math.Min(8, text.Length)] + "..." : string.Empty;

        /// <summary>Updates the button text only when the scene object's name changes.</summary>
        public string GetButtonLabel(string displayName)
        {
            if (_buttonLabel is not null && string.Equals(_buttonDisplayName, displayName, StringComparison.Ordinal))
                return _buttonLabel;

            _buttonDisplayName = displayName;
            _buttonLabel = $"Select node: {displayName}##{Text}";
            return _buttonLabel;
        }
    }
}

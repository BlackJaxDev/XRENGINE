namespace XREngine.Editor.UI.Tools;

public sealed partial class McpAssistantWindow
{
    /// <summary>
    /// Result from an MCP tool call, carrying the text result and optional image data
    /// for multimodal feedback to the model.
    /// </summary>
    private readonly record struct ToolCallResult(
        string Text,
        string? ImageBase64 = null,
        string? ImagePath = null)
    {
        public bool HasImage => !string.IsNullOrEmpty(ImageBase64);
        public bool IsError => Text.StartsWith("[MCP Error]", StringComparison.Ordinal);
    }
}

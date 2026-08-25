namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Rendered Markdown text plus the presentation runs applied to it.</summary>
internal sealed record MarkdownPreviewDocument(
    string Text,
    IReadOnlyList<MarkdownPreviewRun> Runs)
{
    public static MarkdownPreviewDocument Empty { get; } = new(string.Empty, []);
}

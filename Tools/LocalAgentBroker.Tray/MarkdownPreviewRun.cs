namespace XREngine.LocalAgentBroker.Tray;

/// <summary>One contiguous presentation run in a rendered Markdown preview.</summary>
internal readonly record struct MarkdownPreviewRun(
    int Start,
    int Length,
    MarkdownPreviewStyle Style);

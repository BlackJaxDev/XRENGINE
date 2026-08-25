namespace XREngine.LocalAgentBroker.Tray;

/// <summary>Viewport and selection state captured before a streaming update.</summary>
internal readonly record struct RichTextUpdateState(
    Point ScrollPosition,
    int SelectionStart,
    int SelectionLength,
    bool FollowTail);

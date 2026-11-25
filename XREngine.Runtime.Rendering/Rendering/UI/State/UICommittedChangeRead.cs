namespace XREngine.Rendering.UI.State;

/// <summary>Describes one atomic read of committed publication changes.</summary>
public readonly record struct UICommittedChangeRead(
    UICommittedChangeReadStatus Status,
    UICommittedChangeCursor EndCursor,
    int Count,
    UIChangeDomain Domains);

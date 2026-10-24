namespace XREngine.Rendering;

/// <summary>Identifies one native close request that passed window close policy.</summary>
public readonly record struct WindowCloseApproval(
    ulong RequestSequence,
    bool ApplicationShutdownIntent);

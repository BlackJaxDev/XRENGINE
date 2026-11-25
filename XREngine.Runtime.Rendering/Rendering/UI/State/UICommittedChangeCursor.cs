namespace XREngine.Rendering.UI.State;

/// <summary>Names one committed position in one UI change journal.</summary>
public readonly record struct UICommittedChangeCursor(
    long JournalId, long Sequence, long Revision);

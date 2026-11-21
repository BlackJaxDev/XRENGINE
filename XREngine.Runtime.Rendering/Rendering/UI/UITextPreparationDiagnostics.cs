namespace XREngine.Rendering.UI;

/// <summary>
/// Reports cumulative text preparation attempts. Values can advance while a snapshot is read.
/// </summary>
public readonly record struct UITextPreparationDiagnostics(
    long GroupCalls,
    long EmptyGroupCalls,
    long PreparationCalls,
    long PreparationFailures,
    ulong LastAttemptFrame,
    ulong LastPreparedFrame,
    int LastGlyphCount,
    string LastFailureReason);

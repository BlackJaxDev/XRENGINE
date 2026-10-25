namespace XREngine.Editor;

/// <summary>Immutable panel values and the captured source's validity proof.</summary>
internal readonly record struct NativeDockPanelTransferState(
    NativeDockPanelStateValues Values,
    Func<bool> RequireSourceCurrent)
{
    internal bool IsCurrent => RequireSourceCurrent();
}

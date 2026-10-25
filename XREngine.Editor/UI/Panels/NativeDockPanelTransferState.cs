namespace XREngine.Editor;

/// <summary>Immutable panel values and the captured source's validity proof.</summary>
internal readonly record struct NativeDockPanelTransferState(
    NativeDockPanelStateValues Values,
    Func<bool> RequireSourceCurrent,
    NativeAnimationPanelViewValues? AnimationView = null)
{
    internal bool IsCurrent => RequireSourceCurrent();
}

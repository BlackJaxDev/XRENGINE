namespace XREngine.Editor;

/// <summary>Immutable panel values and the captured source's validity proof.</summary>
internal readonly record struct NativeDockPanelTransferState(
    NativeDockPanelStateValues Values,
    Func<bool> RequireSourceCurrent,
    NativeAnimationPanelViewValues? AnimationView = null,
    NativeHierarchyPanelViewValues? HierarchyView = null,
    NativeTextFieldDraft? FieldDraft = null)
{
    internal bool IsCurrent => RequireSourceCurrent();
}

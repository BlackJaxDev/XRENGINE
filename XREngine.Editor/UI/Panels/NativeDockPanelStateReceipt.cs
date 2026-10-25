namespace XREngine.Editor;

/// <summary>Checks one captured live panel state without trusting later layout values.</summary>
internal readonly record struct NativeDockPanelStateReceipt(
    NativeToolPanelStateBinding Binding,
    NativeDockPanelStateValues Values,
    ulong IntentRevision,
    ulong StateRevision,
    ulong ScrollIntentRevision,
    NativePanelScrollOwnerReceipt? ScrollOwner)
{
    internal bool IsCurrent => Binding.IsCurrent &&
        Binding.Panel.StateIntentRevision == IntentRevision &&
        Binding.Panel.StateRevision == StateRevision &&
        Binding.Panel.ScrollIntentRevision == ScrollIntentRevision &&
        Binding.Panel.HasCurrentScrollOwner(ScrollOwner) &&
        Values.Matches(Binding.CaptureLiveValues());
}

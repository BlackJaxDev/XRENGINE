using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Pins the panel, dock ancestry, document, and accepted hierarchy view.</summary>
internal sealed class NativeHierarchyPanelViewReceipt
{
    private readonly EditorVirtualRowControlLease _path;
    private readonly Func<bool> _geometryCurrent;
    private readonly ObjectConstructionIdentity _panelIdentity;
    private readonly ObjectConstructionIdentity _nodeIdentity;
    private readonly ObjectConstructionIdentity _transformIdentity;
    private readonly ObjectConstructionIdentity _slotIdentity;
    private readonly ObjectConstructionIdentity _slotTransformIdentity;

    internal NativeHierarchyPanelViewReceipt(HierarchyPanel panel, SceneNode slot,
        SceneNode hostRoot, NativeHierarchyPanelViewValues values, ulong intent,
        uint lifecycle, NativePanelScrollOwnerReceipt? scroll, ulong scrollIntent,
        UIVirtualTreeProvider? provider, ulong providerRevision, ulong providerIncarnation,
        Func<bool> geometryCurrent)
    {
        _geometryCurrent = geometryCurrent;
        Panel = panel;
        Slot = slot;
        HostRoot = hostRoot;
        Values = values;
        Intent = intent;
        Lifecycle = lifecycle;
        Scroll = scroll;
        ScrollIntent = scrollIntent;
        Provider = provider;
        ProviderRevision = providerRevision;
        ProviderIncarnation = providerIncarnation;
        _path = EditorVirtualRowControlLease.Capture(panel, hostRoot);
        _panelIdentity = ObjectConstructionIdentity.Capture(panel);
        _nodeIdentity = ObjectConstructionIdentity.Capture(panel.SceneNode);
        _transformIdentity = ObjectConstructionIdentity.Capture(panel.Transform);
        _slotIdentity = ObjectConstructionIdentity.Capture(slot);
        _slotTransformIdentity = ObjectConstructionIdentity.Capture(slot.Transform);
    }

    internal HierarchyPanel Panel { get; }
    internal SceneNode Slot { get; }
    internal SceneNode HostRoot { get; }
    internal NativeHierarchyPanelViewValues Values { get; }
    internal ulong Intent { get; }
    internal uint Lifecycle { get; }
    internal NativePanelScrollOwnerReceipt? Scroll { get; }
    internal ulong ScrollIntent { get; }
    internal UIVirtualTreeProvider? Provider { get; }
    internal ulong ProviderRevision { get; }
    internal ulong ProviderIncarnation { get; }

    // A dock move may reparent the retained slot and activate it. Only that
    // operation may use this narrower proof before it captures its new path.
    internal bool HasSamePanelIntent => _panelIdentity.IsCurrent && _nodeIdentity.IsCurrent &&
        _transformIdentity.IsCurrent && _slotIdentity.IsCurrent && _slotTransformIdentity.IsCurrent &&
        !Panel.IsDestroyed && !Panel.IsDestroyQueued && !Panel.SceneNode.IsDestroyed &&
        !Panel.SceneNode.IsDestroyQueued && !Panel.Transform.IsDestroyed && !Panel.Transform.IsDestroyQueued &&
        !Slot.IsDestroyed && !Slot.IsDestroyQueued && !Slot.Transform.IsDestroyed && !Slot.Transform.IsDestroyQueued &&
        ReferenceEquals(Panel.SceneNode, _nodeIdentity.Target) &&
        ReferenceEquals(Panel.Transform, _transformIdentity.Target) &&
        ReferenceEquals(Slot.Transform, _slotTransformIdentity.Target) &&
        ReferenceEquals(Panel.SceneNode.Parent, Slot) &&
        Panel.HierarchyViewIntent == Intent && Panel.HasHierarchyViewDocument(Values) &&
        (Scroll is not { } scroll || scroll.List.ContentScrollIntentRevision == ScrollIntent);

    internal bool IsCurrent => HasSamePanelIntent && _path.IsCurrent(HostRoot) && _geometryCurrent() &&
        Panel.IsHierarchyViewCurrent(this);
}

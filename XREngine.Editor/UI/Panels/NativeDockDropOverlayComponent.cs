using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>
/// Shows the dock drop overlay of its stack while a panel drag is active and collapses it at other times. A
/// collapsed overlay has no hit records, so it does not take input from the panel content under it.
/// </summary>
internal sealed class NativeDockDropOverlayComponent : XRComponent
{
    private readonly WorldTick _tick;

    public NativeDockDropOverlayComponent() => _tick = OnTick;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, _tick);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, _tick);
        base.OnComponentDeactivated();
    }

    private void OnTick()
    {
        if (SceneNode?.Transform is not UIBoundableTransform overlay)
            return;
        bool dragging = EditorDragDropUtility.TryGetActivePayload(out EditorDragDropUtility.DragPayload payload) &&
            (payload.TypeId == NativeDockTabPayload.DragType || payload.TypeId == NativeDockLocalTabPayload.DragType);
        EVisibility visibility = dragging ? EVisibility.Visible : EVisibility.Collapsed;
        if (overlay.Visibility != visibility)
            overlay.Visibility = visibility;
    }
}

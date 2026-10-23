using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    private UICanvasComponent? _pendingVirtualFocusCanvas;
    private UIElementIdentity _pendingVirtualFocusListIdentity;
    private ulong _pendingVirtualFocusItemId;
    private long _pendingVirtualFocusRevision;
    private ulong _pendingVirtualFocusLifetime;
    private bool _resolvingVirtualFocus;

    /// <summary>Scrolls to a virtual item and focuses it only after a matching row is presented.</summary>
    public bool RequestVirtualItemFocus(UIListTransform list, ulong itemId, bool center = false)
    {
        ArgumentNullException.ThrowIfNull(list);
        UICanvasComponent? canvas = GetCameraCanvas();
        if (!IsActiveInHierarchy || canvas is null || !ReferenceEquals(list.ParentCanvas, canvas.CanvasTransform) ||
            !list.TryScrollToVirtualItem(itemId, center))
            return false;

        _pendingVirtualFocusCanvas = canvas;
        _pendingVirtualFocusListIdentity = list.ElementIdentity;
        _pendingVirtualFocusItemId = itemId;
        _pendingVirtualFocusRevision = canvas.CanvasTransform.ChangeJournal.Revision;
        _pendingVirtualFocusLifetime = canvas.PublicationLifetime;
        return true;
    }

    private void ResolvePresentedVirtualFocus(UIPublication? publication)
    {
        if (_resolvingVirtualFocus || publication is null || _pendingVirtualFocusItemId == 0)
            return;
        if (!ReferenceEquals(_pendingVirtualFocusCanvas, GetCameraCanvas()) ||
            publication.CanvasLifetime != _pendingVirtualFocusLifetime)
        {
            ClearPendingVirtualFocus();
            return;
        }
        if (publication.ChangeRevision < _pendingVirtualFocusRevision)
            return;

        bool listStillPresented = false;
        foreach (UIElementIdentity identity in publication.Identities.Span)
            if (identity == _pendingVirtualFocusListIdentity)
            {
                listStillPresented = true;
                break;
            }
        if (publication.Identities.Length > 0 && !listStillPresented)
        {
            ClearPendingVirtualFocus();
            return;
        }

        foreach (ref readonly UIHitRecord record in publication.Hits)
        {
            if (record.VirtualListIdentity != _pendingVirtualFocusListIdentity ||
                !record.VirtualRowIdentity.IsValid ||
                record.VirtualRowLease.ItemId != _pendingVirtualFocusItemId ||
                record.VirtualRowLease.Incarnation == 0 ||
                record.FocusScopeIdentity != publication.ActiveFocusScopeIdentity || !record.IsTabStop ||
                !IsCurrentHitRecord(record) || record.RenderInfo.Owner is not UIInteractableComponent target)
                continue;

            ClearPendingVirtualFocus();
            _resolvingVirtualFocus = true;
            try { FocusMouseTarget(target); }
            finally { _resolvingVirtualFocus = false; }
            return;
        }
    }

    private void ClearPendingVirtualFocus()
    {
        _pendingVirtualFocusCanvas = null;
        _pendingVirtualFocusListIdentity = default;
        _pendingVirtualFocusItemId = 0;
    }

    private void CancelVirtualFocusAfterExternalFocusChange()
    {
        if (!_resolvingVirtualFocus && FocusedComponent is not null)
            ClearPendingVirtualFocus();
    }
}

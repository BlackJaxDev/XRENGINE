using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    private UICanvasComponent? _pendingVirtualFocusCanvas;
    private UIListTransform? _pendingVirtualFocusList;
    private UIElementIdentity _pendingVirtualFocusListIdentity;
    private ulong _pendingVirtualFocusItemId;
    private long _pendingVirtualFocusRevision;
    private ulong _pendingVirtualFocusLifetime;
    private ulong _pendingVirtualFocusProviderIncarnation;
    private UIElementIdentity _pendingVirtualFocusScopeIdentity;
    private bool _resolvingVirtualFocus;
    private UIListTransform? _acceptingVirtualFocusList;
    private UIInteractableComponent? _acceptingVirtualFocusTarget;
    private UIElementIdentity _acceptingVirtualFocusListIdentity;
    private UIElementIdentity _acceptingVirtualFocusRowIdentity;
    private UIElementIdentity _acceptingVirtualFocusTargetIdentity;
    private UIVirtualRowLease _acceptingVirtualFocusLease;
    private ulong _acceptingVirtualFocusProviderIncarnation;
    private ulong _acceptingVirtualFocusCanvasLifetime;
    private ulong _acceptingVirtualFocusEpoch;
    private ulong _virtualFocusRequestEpoch;

    /// <summary>Scrolls to a virtual item and focuses it only after a matching row is presented.</summary>
    public bool RequestVirtualItemFocus(UIListTransform list, ulong itemId, bool center = false)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_inputTerminal || _inputDeactivating)
            return false;
        ClearPendingVirtualFocus();
        ulong requestEpoch = _virtualFocusRequestEpoch;
        UICanvasComponent? canvas = GetCameraCanvas();
        IVirtualListProvider? provider = list.VirtualProvider;
        UIElementIdentity listIdentity = list.ElementIdentity;
        ulong providerIncarnation = list.VirtualProviderIncarnation;
        ulong canvasLifetime = canvas?.PublicationLifetime ?? 0;
        UIElementIdentity focusScope = ReferenceEquals(_inputCanvas, canvas) &&
            _inputPublication is { } presented &&
            presented.CanvasLifetime == canvasLifetime
            ? presented.ActiveFocusScopeIdentity : canvas?.CanvasTransform.ElementIdentity ?? default;
        if (_inputTerminal || _inputDeactivating || !IsActiveInHierarchy || canvas is null ||
            !ReferenceEquals(list.ParentCanvas, canvas.CanvasTransform) ||
            provider is null || !list.TryScrollToVirtualItem(itemId, center) ||
            requestEpoch != _virtualFocusRequestEpoch || _inputTerminal || _inputDeactivating ||
            !IsActiveInHierarchy ||
            !ReferenceEquals(GetCameraCanvas(), canvas) || canvas.PublicationLifetime != canvasLifetime ||
            list.IsDestroyed || list.IsDestroyQueued || list.ElementIdentity != listIdentity ||
            !ReferenceEquals(list.ParentCanvas, canvas.CanvasTransform) ||
            !ReferenceEquals(list.VirtualProvider, provider) ||
            list.VirtualProviderIncarnation != providerIncarnation ||
            !ProviderStillContainsItem(provider, itemId) ||
            requestEpoch != _virtualFocusRequestEpoch ||
            _inputTerminal || _inputDeactivating || !IsActiveInHierarchy ||
            !ReferenceEquals(GetCameraCanvas(), canvas) ||
            canvas.PublicationLifetime != canvasLifetime || list.IsDestroyed || list.IsDestroyQueued ||
            list.ElementIdentity != listIdentity ||
            !ReferenceEquals(list.ParentCanvas, canvas.CanvasTransform) ||
            !ReferenceEquals(list.VirtualProvider, provider) ||
            list.VirtualProviderIncarnation != providerIncarnation)
            return false;

        _pendingVirtualFocusCanvas = canvas;
        _pendingVirtualFocusList = list;
        _pendingVirtualFocusListIdentity = listIdentity;
        _pendingVirtualFocusItemId = itemId;
        _pendingVirtualFocusRevision = canvas.CanvasTransform.ChangeJournal.Revision;
        _pendingVirtualFocusLifetime = canvasLifetime;
        _pendingVirtualFocusProviderIncarnation = providerIncarnation;
        _pendingVirtualFocusScopeIdentity = focusScope;
        return true;
    }

    private void ResolvePresentedVirtualFocus(UIPublication? publication)
    {
        if (_resolvingVirtualFocus || publication is null || _pendingVirtualFocusItemId == 0)
            return;
        ulong requestEpoch = _virtualFocusRequestEpoch;
        if (publication.ActiveFocusScopeIdentity != _pendingVirtualFocusScopeIdentity)
        {
            ClearPendingVirtualFocus();
            return;
        }
        if (!ReferenceEquals(_pendingVirtualFocusCanvas, GetCameraCanvas()) ||
            publication.CanvasLifetime != _pendingVirtualFocusLifetime)
        {
            ClearPendingVirtualFocus();
            return;
        }
        if (_pendingVirtualFocusList is not { } list || list.IsDestroyed || list.IsDestroyQueued ||
            list.ElementIdentity != _pendingVirtualFocusListIdentity ||
            !ReferenceEquals(list.ParentCanvas, _pendingVirtualFocusCanvas?.CanvasTransform) ||
            list.VirtualProviderIncarnation != _pendingVirtualFocusProviderIncarnation ||
            list.VirtualProvider is not { } provider)
        {
            ClearPendingVirtualFocus();
            return;
        }
        bool itemStillExists = ProviderStillContainsItem(provider, _pendingVirtualFocusItemId);
        if (requestEpoch != _virtualFocusRequestEpoch)
            return;
        if (!itemStillExists || list.IsDestroyed || list.IsDestroyQueued ||
            list.ElementIdentity != _pendingVirtualFocusListIdentity ||
            !ReferenceEquals(list.ParentCanvas, _pendingVirtualFocusCanvas?.CanvasTransform) ||
            !ReferenceEquals(list.VirtualProvider, provider))
        {
            ClearPendingVirtualFocus();
            return;
        }
        if (requestEpoch != _virtualFocusRequestEpoch ||
            list.VirtualProviderIncarnation != _pendingVirtualFocusProviderIncarnation)
            return;
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
                record.VirtualProviderIncarnation != _pendingVirtualFocusProviderIncarnation ||
                requestEpoch != _virtualFocusRequestEpoch ||
                record.FocusScopeIdentity != publication.ActiveFocusScopeIdentity || !record.IsTabStop ||
                !IsCurrentHitRecord(record) || record.RenderInfo.Owner is not UIInteractableComponent target)
                continue;

            UIListTransform acceptedList = list;
            ulong acceptedProviderIncarnation = _pendingVirtualFocusProviderIncarnation;
            ulong acceptedLifetime = _pendingVirtualFocusLifetime;
            ClearPendingVirtualFocus();
            _acceptingVirtualFocusList = acceptedList;
            _acceptingVirtualFocusTarget = target;
            _acceptingVirtualFocusListIdentity = record.VirtualListIdentity;
            _acceptingVirtualFocusRowIdentity = record.VirtualRowIdentity;
            _acceptingVirtualFocusTargetIdentity = record.Identity;
            _acceptingVirtualFocusLease = record.VirtualRowLease;
            _acceptingVirtualFocusProviderIncarnation = acceptedProviderIncarnation;
            _acceptingVirtualFocusCanvasLifetime = acceptedLifetime;
            _acceptingVirtualFocusEpoch = _virtualFocusRequestEpoch;
            _resolvingVirtualFocus = true;
            try { FocusMouseTarget(target); }
            finally
            {
                _resolvingVirtualFocus = false;
                _acceptingVirtualFocusList = null;
                _acceptingVirtualFocusTarget = null;
                _acceptingVirtualFocusListIdentity = default;
                _acceptingVirtualFocusRowIdentity = default;
                _acceptingVirtualFocusTargetIdentity = default;
                _acceptingVirtualFocusLease = default;
                _acceptingVirtualFocusProviderIncarnation = 0;
                _acceptingVirtualFocusCanvasLifetime = 0;
                _acceptingVirtualFocusEpoch = 0;
            }
            return;
        }
    }

    public bool IsDispatchingVirtualItemFocus(UIListTransform list, ulong itemId,
        UIInteractableComponent target)
        => _resolvingVirtualFocus && !_inputTerminal && !_inputDeactivating &&
            !list.IsDestroyed && !list.IsDestroyQueued &&
            !target.IsDestroyed && !target.IsDestroyQueued &&
            _acceptingVirtualFocusEpoch == _virtualFocusRequestEpoch &&
            ReferenceEquals(_acceptingVirtualFocusList, list) &&
            ReferenceEquals(_acceptingVirtualFocusTarget, target) &&
            _acceptingVirtualFocusLease.ItemId == itemId &&
            list.ElementIdentity == _acceptingVirtualFocusListIdentity &&
            target.UITransform.ElementIdentity == _acceptingVirtualFocusTargetIdentity &&
            ReferenceEquals(list.ParentCanvas, GetCameraCanvas()?.CanvasTransform) &&
            GetCameraCanvas()?.PublicationLifetime == _acceptingVirtualFocusCanvasLifetime &&
            list.VirtualProviderIncarnation == _acceptingVirtualFocusProviderIncarnation &&
            list.TryGetRealizedVirtualRow(itemId, out UIBoundableTransform? row,
                out UIVirtualRowLease lease) &&
            row?.ElementIdentity == _acceptingVirtualFocusRowIdentity &&
            lease == _acceptingVirtualFocusLease;

    private void ClearPendingVirtualFocus()
    {
        _virtualFocusRequestEpoch++;
        _pendingVirtualFocusCanvas = null;
        _pendingVirtualFocusList = null;
        _pendingVirtualFocusListIdentity = default;
        _pendingVirtualFocusItemId = 0;
        _pendingVirtualFocusProviderIncarnation = 0;
        _pendingVirtualFocusScopeIdentity = default;
    }

    private static bool ProviderStillContainsItem(IVirtualListProvider provider, ulong itemId)
    {
        ulong revision = provider.Revision;
        return itemId != 0 && provider is IVirtualListItemIndex lookup &&
            lookup.TryGetIndexOfItemId(itemId, out int index) &&
            (uint)index < (uint)provider.Count && provider.GetItemId(index) == itemId &&
            provider.Revision == revision;
    }

    protected override void OnDestroying()
    {
        _inputTerminal = true;
        LeftClickDown = null;
        RightClick = null;
        EscapePressed = null;
        BackPressed = null;
        List<Exception>? failures = null;
        TryInputCleanup(TeardownInputOwners, ref failures);
        TryInputCleanup(() => base.OnDestroying(), ref failures);
        ThrowInputCleanupFailures(failures);
    }

    private void CancelVirtualFocusAfterExternalFocusChange()
    {
        if (!_resolvingVirtualFocus && FocusedComponent is not null)
            ClearPendingVirtualFocus();
    }
}

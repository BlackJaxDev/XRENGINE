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
    private Func<bool>? _pendingVirtualFocusProof;
    private UIInteractableComponent? _pendingVirtualFocusExpectedTarget;
    private ulong _pendingVirtualFocusIntentRevision;
    private UICanvasFocusOwnerProof? _pendingVirtualFocusOwner;
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
    private Func<bool>? _acceptingVirtualFocusProof;
    private UIInteractableComponent? _acceptingVirtualFocusExpectedTarget;
    private ulong _acceptingVirtualFocusIntentRevision;
    private UICanvasFocusOwnerProof? _acceptingVirtualFocusOwner;
    private ulong _virtualFocusRequestEpoch;

    /// <summary>Scrolls to a virtual item and focuses it only after a matching row is presented.</summary>
    public bool RequestVirtualItemFocus(UIListTransform list, ulong itemId, bool center = false)
        => RequestVirtualItemFocusCore(list, itemId, null, null, null, center);

    public bool RequestVirtualItemFocus(UIListTransform list, ulong itemId,
        Func<bool> isCurrent, bool center = false)
        => RequestVirtualItemFocusCore(list, itemId, null,
            isCurrent ?? throw new ArgumentNullException(nameof(isCurrent)), null, center);

    public bool RequestVirtualItemFocus(UIListTransform list, ulong itemId,
        UIInteractableComponent expectedTarget, Func<bool> isCurrent, bool center = false)
        => RequestVirtualItemFocusCore(list, itemId,
            expectedTarget ?? throw new ArgumentNullException(nameof(expectedTarget)),
            isCurrent ?? throw new ArgumentNullException(nameof(isCurrent)), null, center);

    public bool RequestVirtualItemFocus(UIListTransform list, ulong itemId,
        UIInteractableComponent expectedTarget, ulong expectedFocusIntentRevision,
        Func<bool> isCurrent, bool center = false)
        => RequestVirtualItemFocusCore(list, itemId,
            expectedTarget ?? throw new ArgumentNullException(nameof(expectedTarget)),
            isCurrent ?? throw new ArgumentNullException(nameof(isCurrent)),
            expectedFocusIntentRevision, center);

    private bool RequestVirtualItemFocusCore(UIListTransform list, ulong itemId,
        UIInteractableComponent? expectedTarget, Func<bool>? proof,
        ulong? expectedFocusIntentRevision, bool center)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_inputTerminal || _inputDeactivating ||
            proof is not null && (!ProofCurrent(proof) ||
                expectedFocusIntentRevision is { } expected &&
                _focusIntentRevision != expected))
            return false;
        AdvanceFocusIntent();
        ClearPendingVirtualFocus();
        ulong requestEpoch = _virtualFocusRequestEpoch;
        ulong focusIntent = _focusIntentRevision;
        bool Current() => requestEpoch == _virtualFocusRequestEpoch &&
            (proof is null || _focusIntentRevision == focusIntent && ProofCurrent(proof) &&
                requestEpoch == _virtualFocusRequestEpoch && _focusIntentRevision == focusIntent);
        if (!Current()) return false;
        UICanvasComponent? canvas = proof is null ? GetCameraCanvas() :
            GetCameraCanvasNonCreating();
        if (!Current()) return false;
        UICanvasFocusOwnerProof? owner = proof is null || canvas is null ? null :
            UICanvasFocusOwnerProof.Capture(this, list, canvas, expectedTarget);
        if (proof is not null && (owner is null || !owner.Value.IsCurrent)) return false;
        IVirtualListProvider? provider = list.VirtualProvider;
        if (!Current()) return false;
        UIElementIdentity listIdentity = list.ElementIdentity;
        ulong providerIncarnation = list.VirtualProviderIncarnation;
        ulong canvasLifetime = canvas?.PublicationLifetime ?? 0;
        UIElementIdentity focusScope = ReferenceEquals(_inputCanvas, canvas) &&
            _inputPublication is { } presented &&
            presented.CanvasLifetime == canvasLifetime
            ? presented.ActiveFocusScopeIdentity : canvas?.CanvasTransform.ElementIdentity ?? default;
        if (_inputTerminal || _inputDeactivating || !IsActiveInHierarchy || canvas is null ||
            !ReferenceEquals(list.ParentCanvas, canvas.CanvasTransform) ||
            provider is null || !Current() || !list.TryScrollToVirtualItem(itemId, center) ||
            !Current() ||
            requestEpoch != _virtualFocusRequestEpoch || _inputTerminal || _inputDeactivating ||
            !IsActiveInHierarchy ||
            !ReferenceEquals(proof is null ? GetCameraCanvas() :
                GetCameraCanvasNonCreating(), canvas) ||
            canvas.PublicationLifetime != canvasLifetime ||
            list.IsDestroyed || list.IsDestroyQueued || list.ElementIdentity != listIdentity ||
            !ReferenceEquals(list.ParentCanvas, canvas.CanvasTransform) ||
            !ReferenceEquals(list.VirtualProvider, provider) ||
            list.VirtualProviderIncarnation != providerIncarnation ||
            !Current() || !ProviderStillContainsItem(provider, itemId) || !Current() ||
            requestEpoch != _virtualFocusRequestEpoch ||
            _inputTerminal || _inputDeactivating || !IsActiveInHierarchy ||
            !ReferenceEquals(proof is null ? GetCameraCanvas() :
                GetCameraCanvasNonCreating(), canvas) ||
            canvas.PublicationLifetime != canvasLifetime || list.IsDestroyed || list.IsDestroyQueued ||
            list.ElementIdentity != listIdentity ||
            !ReferenceEquals(list.ParentCanvas, canvas.CanvasTransform) ||
            !ReferenceEquals(list.VirtualProvider, provider) ||
            list.VirtualProviderIncarnation != providerIncarnation || !Current() ||
            owner is { IsCurrent: false })
            return false;

        _pendingVirtualFocusCanvas = canvas;
        _pendingVirtualFocusList = list;
        _pendingVirtualFocusListIdentity = listIdentity;
        _pendingVirtualFocusItemId = itemId;
        _pendingVirtualFocusRevision = canvas.CanvasTransform.ChangeJournal.Revision;
        _pendingVirtualFocusLifetime = canvasLifetime;
        _pendingVirtualFocusProviderIncarnation = providerIncarnation;
        _pendingVirtualFocusScopeIdentity = focusScope;
        _pendingVirtualFocusProof = proof;
        _pendingVirtualFocusExpectedTarget = expectedTarget;
        _pendingVirtualFocusIntentRevision = focusIntent;
        _pendingVirtualFocusOwner = owner;
        return true;
    }

    private static bool ProofCurrent(Func<bool> proof)
    {
        try { return proof(); }
        catch { return false; }
    }

    private bool PendingVirtualFocusCurrent(ulong requestEpoch)
        => requestEpoch == _virtualFocusRequestEpoch &&
            (_pendingVirtualFocusOwner is null || _pendingVirtualFocusOwner.Value.IsCurrent) &&
            (_pendingVirtualFocusProof is not { } proof ||
                _focusIntentRevision == _pendingVirtualFocusIntentRevision &&
                ProofCurrent(proof) && requestEpoch == _virtualFocusRequestEpoch &&
                _focusIntentRevision == _pendingVirtualFocusIntentRevision);

    private void ResolvePresentedVirtualFocus(UIPublication? publication)
    {
        if (_resolvingVirtualFocus || publication is null || _pendingVirtualFocusItemId == 0)
            return;
        ulong requestEpoch = _virtualFocusRequestEpoch;
        if (!PendingVirtualFocusCurrent(requestEpoch))
        {
            if (requestEpoch == _virtualFocusRequestEpoch) ClearPendingVirtualFocus();
            return;
        }
        if (publication.ActiveFocusScopeIdentity != _pendingVirtualFocusScopeIdentity)
        {
            if (requestEpoch == _virtualFocusRequestEpoch) ClearPendingVirtualFocus();
            return;
        }
        if (!ReferenceEquals(_pendingVirtualFocusCanvas,
                _pendingVirtualFocusProof is null ? GetCameraCanvas() :
                    GetCameraCanvasNonCreating()) ||
            publication.CanvasLifetime != _pendingVirtualFocusLifetime)
        {
            if (requestEpoch == _virtualFocusRequestEpoch) ClearPendingVirtualFocus();
            return;
        }
        if (_pendingVirtualFocusList is not { } list || list.IsDestroyed || list.IsDestroyQueued ||
            list.ElementIdentity != _pendingVirtualFocusListIdentity ||
            !ReferenceEquals(list.ParentCanvas, _pendingVirtualFocusCanvas?.CanvasTransform) ||
            list.VirtualProviderIncarnation != _pendingVirtualFocusProviderIncarnation ||
            list.VirtualProvider is not { } provider)
        {
            if (requestEpoch == _virtualFocusRequestEpoch) ClearPendingVirtualFocus();
            return;
        }
        bool itemStillExists = ProviderStillContainsItem(provider, _pendingVirtualFocusItemId);
        if (!PendingVirtualFocusCurrent(requestEpoch))
        {
            if (requestEpoch == _virtualFocusRequestEpoch) ClearPendingVirtualFocus();
            return;
        }
        if (!itemStillExists || list.IsDestroyed || list.IsDestroyQueued ||
            list.ElementIdentity != _pendingVirtualFocusListIdentity ||
            !ReferenceEquals(list.ParentCanvas, _pendingVirtualFocusCanvas?.CanvasTransform) ||
            !ReferenceEquals(list.VirtualProvider, provider))
        {
            if (requestEpoch == _virtualFocusRequestEpoch) ClearPendingVirtualFocus();
            return;
        }
        if (!PendingVirtualFocusCurrent(requestEpoch) ||
            list.VirtualProviderIncarnation != _pendingVirtualFocusProviderIncarnation)
        {
            if (requestEpoch == _virtualFocusRequestEpoch) ClearPendingVirtualFocus();
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
            if (requestEpoch == _virtualFocusRequestEpoch) ClearPendingVirtualFocus();
            return;
        }

        for (int page = 0; page < publication.HitRecordPageCount; page++)
            foreach (ref readonly UIHitRecord record in publication.GetHitRecordPage(page))
            {
                if (record.VirtualListIdentity != _pendingVirtualFocusListIdentity ||
                    !record.VirtualRowIdentity.IsValid ||
                    record.VirtualRowLease.ItemId != _pendingVirtualFocusItemId ||
                    record.VirtualRowLease.Incarnation == 0 ||
                    record.VirtualProviderIncarnation != _pendingVirtualFocusProviderIncarnation ||
                    !PendingVirtualFocusCurrent(requestEpoch) ||
                    record.FocusScopeIdentity != publication.ActiveFocusScopeIdentity || !record.IsTabStop ||
                    !IsCurrentHitRecord(record) || record.RenderInfo.Owner is not UIInteractableComponent target)
                    continue;

                if (!PendingVirtualFocusCurrent(requestEpoch))
                {
                    if (requestEpoch == _virtualFocusRequestEpoch) ClearPendingVirtualFocus();
                    return;
                }
                if (_pendingVirtualFocusExpectedTarget is { } expected &&
                    !ReferenceEquals(expected, target))
                    continue;

                UIListTransform acceptedList = list;
                ulong acceptedProviderIncarnation = _pendingVirtualFocusProviderIncarnation;
                ulong acceptedLifetime = _pendingVirtualFocusLifetime;
                Func<bool>? acceptedProof = _pendingVirtualFocusProof;
                UIInteractableComponent? acceptedExpectedTarget = _pendingVirtualFocusExpectedTarget;
                ulong acceptedIntent = _pendingVirtualFocusIntentRevision;
                UICanvasFocusOwnerProof? acceptedOwner = _pendingVirtualFocusOwner;
                if (acceptedProof is not null && acceptedExpectedTarget is null)
                    acceptedOwner = UICanvasFocusOwnerProof.Capture(this, list,
                        _pendingVirtualFocusCanvas, target);
                if (acceptedProof is not null && acceptedOwner is not { IsCurrent: true })
                    return;
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
                _acceptingVirtualFocusProof = acceptedProof;
                _acceptingVirtualFocusExpectedTarget = acceptedExpectedTarget;
                _acceptingVirtualFocusIntentRevision = acceptedIntent;
                _acceptingVirtualFocusOwner = acceptedOwner;
                _resolvingVirtualFocus = true;
                try
                {
                    if (acceptedProof is null) FocusMouseTarget(target);
                    else FocusMouseTargetGuarded(target);
                }
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
                    _acceptingVirtualFocusProof = null;
                    _acceptingVirtualFocusExpectedTarget = null;
                    _acceptingVirtualFocusIntentRevision = 0;
                    _acceptingVirtualFocusOwner = null;
                }
                return;
            }
    }

    public bool IsDispatchingVirtualItemFocus(UIListTransform list, ulong itemId,
        UIInteractableComponent target)
    {
        ulong epoch = _acceptingVirtualFocusEpoch;
        ulong intent = _acceptingVirtualFocusIntentRevision;
        IVirtualListProvider? provider = list.VirtualProvider;
        ulong providerIncarnation = list.VirtualProviderIncarnation;
        if (!(_resolvingVirtualFocus && !_inputTerminal && !_inputDeactivating &&
            !list.IsDestroyed && !list.IsDestroyQueued &&
            !target.IsDestroyed && !target.IsDestroyQueued &&
            epoch == _virtualFocusRequestEpoch &&
            (_acceptingVirtualFocusProof is not { } proof ||
                intent == _focusIntentRevision &&
                ProofCurrent(proof) &&
                epoch == _virtualFocusRequestEpoch && intent == _focusIntentRevision) &&
            (_acceptingVirtualFocusOwner is null ||
                _acceptingVirtualFocusOwner.Value.IsCurrent) &&
            (_acceptingVirtualFocusExpectedTarget is null ||
                ReferenceEquals(_acceptingVirtualFocusExpectedTarget, target)) &&
            ReferenceEquals(_acceptingVirtualFocusList, list) &&
            ReferenceEquals(_acceptingVirtualFocusTarget, target) &&
            _acceptingVirtualFocusLease.ItemId == itemId &&
            list.ElementIdentity == _acceptingVirtualFocusListIdentity &&
            target.UITransform.ElementIdentity == _acceptingVirtualFocusTargetIdentity &&
            ReferenceEquals(list.ParentCanvas,
                (_acceptingVirtualFocusProof is null ? GetCameraCanvas() :
                    GetCameraCanvasNonCreating())?.CanvasTransform) &&
            (_acceptingVirtualFocusProof is null ? GetCameraCanvas() :
                GetCameraCanvasNonCreating())?.PublicationLifetime ==
                _acceptingVirtualFocusCanvasLifetime &&
            providerIncarnation == _acceptingVirtualFocusProviderIncarnation &&
            provider is not null))
            return false;
        if (!list.TryGetRealizedVirtualRow(itemId, out UIBoundableTransform? row,
                out UIVirtualRowLease lease))
            return false;
        // A pure caller proof can change during provider callbacks without changing focus intent.
        if (_acceptingVirtualFocusProof is { } postQueryProof &&
            !ProofCurrent(postQueryProof))
            return false;
        // Provider lookups can run user code. Recheck the exact admitted dispatch afterward.
        return _resolvingVirtualFocus && !_inputTerminal && !_inputDeactivating &&
            epoch == _virtualFocusRequestEpoch &&
            (_acceptingVirtualFocusProof is null || intent == _focusIntentRevision) &&
            _acceptingVirtualFocusEpoch == epoch &&
            _acceptingVirtualFocusIntentRevision == intent &&
            ReferenceEquals(_acceptingVirtualFocusList, list) &&
            ReferenceEquals(_acceptingVirtualFocusTarget, target) &&
            (_acceptingVirtualFocusExpectedTarget is null ||
                ReferenceEquals(_acceptingVirtualFocusExpectedTarget, target)) &&
            (_acceptingVirtualFocusOwner is null ||
                _acceptingVirtualFocusOwner.Value.IsCurrent) &&
            !list.IsDestroyed && !list.IsDestroyQueued &&
            !target.IsDestroyed && !target.IsDestroyQueued &&
            ReferenceEquals(list.VirtualProvider, provider) &&
            list.VirtualProviderIncarnation == providerIncarnation &&
            list.ElementIdentity == _acceptingVirtualFocusListIdentity &&
            target.UITransform.ElementIdentity == _acceptingVirtualFocusTargetIdentity &&
            row?.ElementIdentity == _acceptingVirtualFocusRowIdentity &&
            lease == _acceptingVirtualFocusLease;
    }

    private void ClearPendingVirtualFocus()
    {
        _virtualFocusRequestEpoch++;
        _pendingVirtualFocusCanvas = null;
        _pendingVirtualFocusList = null;
        _pendingVirtualFocusListIdentity = default;
        _pendingVirtualFocusItemId = 0;
        _pendingVirtualFocusProviderIncarnation = 0;
        _pendingVirtualFocusScopeIdentity = default;
        _pendingVirtualFocusProof = null;
        _pendingVirtualFocusExpectedTarget = null;
        _pendingVirtualFocusIntentRevision = 0;
        _pendingVirtualFocusOwner = null;
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

}

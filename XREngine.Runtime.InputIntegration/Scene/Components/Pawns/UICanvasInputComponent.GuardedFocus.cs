using System.Runtime.ExceptionServices;
using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    private bool _internalVirtualFocusSetterPermit;
    private Func<bool>? _activeGuardedFocusAdmission;
    private bool _guardedFocusOperationFailed;

    internal UICanvasComponent? GetCameraCanvasNonCreating()
        => Canvas ?? GetSiblingComponent<UICanvasComponent>(false);

    private void SetMouseFocusedComponent(UIInteractableComponent? target)
    {
        _internalVirtualFocusSetterPermit = _resolvingVirtualFocus;
        try { FocusedComponent = target; }
        finally { _internalVirtualFocusSetterPermit = false; }
    }

    private bool GuardedFocusAdmissionCurrent()
        => _activeGuardedFocusAdmission is not { } admission || admission();

    private void FocusMouseTargetGuarded(UIInteractableComponent target)
    {
        bool Current() => _resolvingVirtualFocus &&
            _acceptingVirtualFocusEpoch == _virtualFocusRequestEpoch &&
            _acceptingVirtualFocusIntentRevision == _focusIntentRevision &&
            (_acceptingVirtualFocusExpectedTarget is null ||
                ReferenceEquals(_acceptingVirtualFocusExpectedTarget, target)) &&
            _acceptingVirtualFocusProof is { } proof && ProofCurrent(proof) &&
            IsDispatchingVirtualItemFocus(_acceptingVirtualFocusList!,
                _acceptingVirtualFocusLease.ItemId, target);
        if (!Current()) return;
        var controller = OwningPawn?.Controller;
        if (controller?.FocusedInteractable is UIInteractableComponent previous &&
            !ReferenceEquals(previous, target))
        {
            if (!previous.SetFocusGuarded(false, Current)) return;
            if (!Current() || !ReferenceEquals(controller, OwningPawn?.Controller)) return;
        }
        if (_linkedFocusedComponent is { } linked && !ReferenceEquals(linked, target) &&
            linked.IsFocused)
        {
            if (!linked.SetFocusGuarded(false, Current)) return;
            if (!Current()) return;
        }

        // The accepted dispatch owns one new focus intent before its field callbacks.
        AdvanceFocusIntent();
        ulong committedIntent = _focusIntentRevision;
        _acceptingVirtualFocusIntentRevision = committedIntent;
        _guardedFocusOperationFailed = false;
        bool rejected = false;
        void Require()
        {
            if (Current()) return;
            rejected = true;
            throw new InvalidOperationException("The virtual focus owner changed during assignment.");
        }
        Func<bool>? previousAdmission = _activeGuardedFocusAdmission;
        _activeGuardedFocusAdmission = () => !_guardedFocusOperationFailed && Current();
        try
        {
            try
            {
                bool changed = SetFieldValidated(ref _focusedComponent, target, (Action)Require,
                    (require, original, current, _) =>
                    {
                        require();
                        if (!ReferenceEquals(original, current))
                        {
                            rejected = true;
                            throw new InvalidOperationException("The focused control changed during assignment.");
                        }
                    }, propertyName: nameof(FocusedComponent));
                if (!changed && !_guardedFocusOperationFailed && Current() &&
                    ReferenceEquals(_focusedComponent, target))
                    ReconcileFocusedComponent();
            }
            catch (InvalidOperationException) when (rejected) { }
        }
        finally { _activeGuardedFocusAdmission = previousAdmission; }

        // A callback may have won with a newer intent. Reconcile only that newer work.
        if (_focusIntentRevision != committedIntent)
            ReconcileFocusedComponent();
        else if ((_guardedFocusOperationFailed || !Current()) &&
            ReferenceEquals(_focusedComponent, target))
            TryClearFocus(target, committedIntent, out _);
    }

    public bool TryClearFocus(UIInteractableComponent expectedTarget, ulong expectedIntent,
        out ulong clearedIntent)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        clearedIntent = 0;
        if (expectedIntent == 0 || _focusIntentRevision != expectedIntent ||
            !ReferenceEquals(_focusedComponent, expectedTarget))
            return false;
        UICanvasFocusOwnerProof? owner = UICanvasFocusOwnerProof.Capture(this, null,
            GetCameraCanvasNonCreating(), expectedTarget);
        if (owner is not { IsCurrent: true }) return false;
        AdvanceFocusIntent();
        ulong ownIntent = _focusIntentRevision;
        ClearPendingVirtualFocus();
        _guardedFocusOperationFailed = false;
        bool rejected = false;
        void Require()
        {
            if (owner.Value.IsCurrent && _focusIntentRevision == ownIntent &&
                ReferenceEquals(_focusedComponent, expectedTarget)) return;
            rejected = true;
            throw new InvalidOperationException("The focused control changed during clear.");
        }
        Func<bool>? previousAdmission = _activeGuardedFocusAdmission;
        _activeGuardedFocusAdmission = () =>
            owner.Value.IsCurrent && _focusIntentRevision == ownIntent &&
            _focusedComponent is null;
        bool changed = false;
        Exception? failure = null;
        try
        {
            try
            {
                changed = SetFieldValidated(ref _focusedComponent,
                    (UIInteractableComponent?)null, (Action)Require,
                    (require, original, current, _) =>
                    {
                        require();
                        if (!ReferenceEquals(original, current))
                        {
                            rejected = true;
                            throw new InvalidOperationException("The focused control changed during clear.");
                        }
                    }, propertyName: nameof(FocusedComponent));
            }
            catch (InvalidOperationException) when (rejected) { }
            catch (Exception exception) { failure = exception; }
        }
        finally { _activeGuardedFocusAdmission = previousAdmission; }
        if (_focusIntentRevision != ownIntent)
        {
            try { ReconcileFocusedComponent(); }
            catch when (failure is not null) { }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return false;
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (!changed) return false;
        if (_guardedFocusOperationFailed && owner.Value.IsCurrent &&
            _focusedComponent is null)
            SetField(ref _focusedComponent, expectedTarget,
                publishNotifications: false, propertyName: nameof(FocusedComponent));
        if (_guardedFocusOperationFailed || !owner.Value.IsCurrent ||
            _focusedComponent is not null)
            return false;
        clearedIntent = ownIntent;
        return true;
    }
}

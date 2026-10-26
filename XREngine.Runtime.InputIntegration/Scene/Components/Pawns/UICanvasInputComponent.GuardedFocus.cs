using System.Runtime.ExceptionServices;
using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    private bool _internalVirtualFocusSetterPermit;
    private Func<bool>? _activeGuardedFocusAdmission;
    private bool _guardedFocusOperationFailed;
    private UIFocusWriteReceipt? _focusAcquisitionReceipt;
    private UIFocusWriteReceipt? _capturedFocusRelease;
    private bool _releasingCapturedFocus;
    private Func<bool>? _capturedControlReleaseAdmission;

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
        UICanvasFocusOwnerProof? inputOwner = UICanvasFocusOwnerProof.Capture(this, null,
            GetCameraCanvasNonCreating(), null);
        if (inputOwner is not { IsCurrent: true } || !Current()) return;
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
        UIInteractableComponent? originalFocused = _focusedComponent;
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
        UIFocusWriteReceipt? previousAcquisition = _focusAcquisitionReceipt;
        _focusAcquisitionReceipt = null;
        Exception? failure = null;
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
            catch (Exception exception) { failure = exception; }
        }
        finally { _activeGuardedFocusAdmission = previousAdmission; }

        UIFocusWriteReceipt? acquisition = _focusAcquisitionReceipt;
        _focusAcquisitionReceipt = previousAcquisition;
        try
        {
            // A callback may have won with a newer intent. Reconcile only that newer work.
            if (_focusIntentRevision != committedIntent ||
                inputOwner.Value.IsCurrent && acquisition is { HasNewerFocusIntent: true })
                ReconcileFocusedComponent();
            else if (!ReferenceEquals(originalFocused, target) && (failure is not null || _guardedFocusOperationFailed || !Current()) &&
                ReferenceEquals(_focusedComponent, target))
                TryClearFocusCore(target, committedIntent, out _, inputOwner, acquisition,
                    failedAcquisition: true);
        }
        catch when (failure is not null) { }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public bool TryClearFocus(UIInteractableComponent expectedTarget, ulong expectedIntent,
        out ulong clearedIntent)
        => TryClearFocusCore(expectedTarget, expectedIntent, out clearedIntent, null, null, false);

    /// <summary>
    /// Releases captured focus without changing its draft, caret, or display.
    /// Callback edits cancel the caller receipt while original-owner cleanup can finish.
    /// </summary>
    public bool TryReleaseFocusPreservingDraft(UIInteractableComponent expectedTarget, ulong expectedIntent,
        Func<bool> isCurrent, out ulong clearedIntent)
        => TryClearFocusCore(expectedTarget, expectedIntent, out clearedIntent, null, null,
            false, isCurrent ?? throw new ArgumentNullException(nameof(isCurrent)));

    private bool TryClearFocusCore(UIInteractableComponent expectedTarget, ulong expectedIntent,
        out ulong clearedIntent, UICanvasFocusOwnerProof? retainedOwner,
        UIFocusWriteReceipt? acquisition, bool failedAcquisition, Func<bool>? callerAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        clearedIntent = 0;
        if (expectedIntent == 0 || _focusIntentRevision != expectedIntent ||
            !ReferenceEquals(_focusedComponent, expectedTarget))
            return false;
        bool preservingDraft = callerAdmission is not null;
        bool capturedRelease = failedAcquisition || preservingDraft;
        UICanvasFocusOwnerProof? owner = retainedOwner ?? UICanvasFocusOwnerProof.Capture(this, null,
            GetCameraCanvasNonCreating(), capturedRelease ? null : expectedTarget);
        if (owner is not { IsCurrent: true }) return false;
        UIFocusWriteReceipt? receipt = preservingDraft ? expectedTarget.CaptureFocusRelease() : acquisition;
        if (preservingDraft && receipt is null) return false;
        bool callerRejected = false;
        bool CallerCurrent()
        {
            bool current = !callerRejected && owner.Value.IsCurrent &&
                (!preservingDraft || receipt!.IsCurrent && ProofCurrent(callerAdmission!) && receipt.IsCurrent) &&
                owner.Value.IsCurrent;
            if (preservingDraft && !current) callerRejected = true;
            return current;
        }
        if (!CallerCurrent() || _focusIntentRevision != expectedIntent ||
            !ReferenceEquals(_focusedComponent, expectedTarget)) return false;
        AdvanceFocusIntent();
        ulong ownIntent = _focusIntentRevision;
        ClearPendingVirtualFocus();
        _guardedFocusOperationFailed = false;
        bool rejected = false;
        void Require()
        {
            if (CallerCurrent() && _focusIntentRevision == ownIntent &&
                ReferenceEquals(_focusedComponent, expectedTarget)) return;
            rejected = true;
            throw new InvalidOperationException("The focused control changed during clear.");
        }
        bool CleanupCurrent()
        {
            // Cleanup must not make a previously rejected caller proof current again.
            if (preservingDraft) CallerCurrent();
            return owner.Value.IsCurrent && _focusIntentRevision == ownIntent && _focusedComponent is null;
        }
        bool ControlReleaseCurrent()
        {
            if (preservingDraft) CallerCurrent();
            return owner.Value.IsCurrent &&
                (_focusIntentRevision == ownIntent ? _focusedComponent is null :
                    !ReferenceEquals(_focusedComponent, expectedTarget));
        }
        Func<bool>? previousAdmission = _activeGuardedFocusAdmission;
        Func<bool>? previousControlRelease = _capturedControlReleaseAdmission;
        UIFocusWriteReceipt? previousRelease = _capturedFocusRelease;
        bool previousCapturedRelease = _releasingCapturedFocus;
        _capturedFocusRelease = receipt;
        _releasingCapturedFocus = capturedRelease;
        _capturedControlReleaseAdmission = capturedRelease ? ControlReleaseCurrent : null;
        // After the raw clear, content changes must not prevent original-owner resource cleanup.
        _activeGuardedFocusAdmission = CleanupCurrent;
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
            if (failure is not null && capturedRelease && CleanupCurrent())
            {
                // A canvas observer can throw before reconciliation. The receipt admits cleanup once.
                try { ReconcileFocusedComponent(); }
                catch { }
            }
        }
        finally
        {
            _activeGuardedFocusAdmission = previousAdmission;
            _capturedFocusRelease = previousRelease;
            _releasingCapturedFocus = previousCapturedRelease;
            _capturedControlReleaseAdmission = previousControlRelease;
        }
        bool Finish(bool result)
        {
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }
        if (_focusIntentRevision != ownIntent)
        {
            try { ReconcileFocusedComponent(); }
            catch when (failure is not null) { }
            return Finish(false);
        }
        bool retainedFocus = receipt is { OwnsFocus: true } ||
            receipt is { HasNewerFocusIntent: true } && expectedTarget.IsFocused;
        if (_guardedFocusOperationFailed && owner.Value.IsCurrent && _focusedComponent is null &&
            (!capturedRelease || retainedFocus))
        {
            SetField(ref _focusedComponent, expectedTarget,
                publishNotifications: false, propertyName: nameof(FocusedComponent));
        }
        if (capturedRelease && receipt is not null && retainedFocus && owner.Value.IsCurrent &&
            ReferenceEquals(_focusedComponent, expectedTarget))
        {
            try { ReconcileRetainedFocusAfterVeto(receipt, owner.Value, expectedTarget, ownIntent); }
            catch when (failure is not null) { }
        }
        if (_focusIntentRevision != ownIntent)
        {
            try { ReconcileFocusedComponent(); }
            catch when (failure is not null) { }
            return Finish(false);
        }
        // A thrown post-change callback can prevent SetFieldValidated from returning its result.
        changed |= capturedRelease && _focusedComponent is null && receipt is { AbortCompleted: true };
        if (!changed || _guardedFocusOperationFailed || !CallerCurrent() || _focusedComponent is not null)
            return Finish(false);
        clearedIntent = ownIntent;
        return Finish(true);
    }

    private void ReconcileRetainedFocusAfterVeto(UIFocusWriteReceipt receipt,
        UICanvasFocusOwnerProof owner, UIInteractableComponent target, ulong intent)
    {
        // Complete mappings for the retained focus. Never replay its gain or its content writes.
        Func<bool>? previousAdmission = _activeGuardedFocusAdmission;
        _activeGuardedFocusAdmission = () => owner.IsCurrent &&
            _focusIntentRevision == intent && ReferenceEquals(_focusedComponent, target) &&
            (receipt.OwnsFocus || receipt.HasNewerFocusIntent && target.IsFocused);
        try { ReconcileFocusedComponent(); }
        finally { _activeGuardedFocusAdmission = previousAdmission; }
    }
}

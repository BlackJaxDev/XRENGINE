using System.Runtime.ExceptionServices;
using XREngine.Data.Core;

namespace XREngine.Rendering.UI;

public abstract partial class UIInteractableComponent
{
    private Func<bool>? _activeFocusWriteAdmission;
    private Action? _rejectActiveFocusWrite;
    private ulong _focusWriteIntentRevision;
    private UIFocusWriteReceipt? _activeFocusAcquisition;
    private UIFocusWriteReceipt? _activeFocusAbort;
    /// <summary>Captures an existing focus owner before the host publishes a draft-preserving release.</summary>
    public UIFocusWriteReceipt? CaptureFocusRelease()
    {
        if (!_isFocused || SceneNode is not { IsTransformNull: false } node ||
            !node.Components.Contains(this))
            return null;
        UIFocusWriteReceipt receipt = new(this, _focusWriteIntentRevision) { IsPreservingRelease = true };
        CaptureFocusContent(receipt);
        return receipt.IsCurrent ? receipt : null;
    }

    /// <summary>Captures control-specific content before focus observers run.</summary>
    protected virtual void CaptureFocusContent(UIFocusWriteReceipt receipt) { }

    internal ulong FocusWriteIntentRevision => _focusWriteIntentRevision;
    protected UIFocusWriteReceipt? FocusAcquisition => _activeFocusAcquisition;

    private void AdvanceFocusWriteIntent()
    {
        ulong next = unchecked(_focusWriteIntentRevision + 1);
        SetField(ref _focusWriteIntentRevision, next == 0 ? 1UL : next,
            publishNotifications: false);
    }

    /// <summary>Gets the admission for the current guarded focus change.</summary>
    protected Func<bool>? FocusWriteAdmission => _activeFocusWriteAdmission;

    /// <summary>Checks that the captured focus change still owns its continuation.</summary>
    protected bool IsFocusWriteCurrent(Func<bool>? admission)
        => admission is null || ReferenceEquals(_activeFocusWriteAdmission, admission) &&
            admission() && ReferenceEquals(_activeFocusWriteAdmission, admission);

    /// <summary>Stops the current guarded focus change after a side effect is rejected.</summary>
    protected void RejectFocusWrite() => _rejectActiveFocusWrite?.Invoke();

    /// <summary>Applies focus only while the exact caller admission survives callbacks.</summary>
    public bool SetFocusGuarded(bool value, Func<bool> isCurrent)
        => SetFocusGuarded(value, isCurrent, out _);

    /// <summary>Returns the original acquisition receipt even when a callback rejects focus.</summary>
    public virtual bool SetFocusGuarded(bool value, Func<bool> isCurrent,
        out UIFocusWriteReceipt? acquisition)
    {
        acquisition = null;
        ArgumentNullException.ThrowIfNull(isCurrent);
        if (SceneNode is not { IsTransformNull: false } node ||
            !node.Components.Contains(this))
            return false;
        var transform = node.Transform;
        AdvanceFocusWriteIntent();
        ulong attempt = _focusWriteIntentRevision;
        UIFocusWriteReceipt? receipt = value && !_isFocused
            ? new UIFocusWriteReceipt(this, attempt) : null;
        acquisition = receipt;
        ObjectConstructionIdentity componentIdentity = ObjectConstructionIdentity.Capture(this);
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
        ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(transform);
        if (receipt is not null) CaptureFocusContent(receipt);
        bool rejected = false;
        bool Current() => !rejected && _focusWriteIntentRevision == attempt && componentIdentity.IsCurrent && nodeIdentity.IsCurrent &&
            transformIdentity.IsCurrent && !IsDestroyed && !IsDestroyQueued &&
            !node.IsDestroyed && !node.IsDestroyQueued && !node.IsTransformNull &&
            !transform.IsDestroyed && !transform.IsDestroyQueued &&
            ReferenceEquals(SceneNode, node) && ReferenceEquals(node.Transform, transform) &&
            node.Components.Contains(this) && isCurrent() &&
            !rejected && _focusWriteIntentRevision == attempt && componentIdentity.IsCurrent && nodeIdentity.IsCurrent && transformIdentity.IsCurrent &&
            ReferenceEquals(SceneNode, node) && !node.IsTransformNull &&
            ReferenceEquals(node.Transform, transform) && node.Components.Contains(this);
        void Require()
        {
            if (Current()) return;
            rejected = true;
            throw new InvalidOperationException("The focused control owner changed.");
        }
        Func<bool>? previousAdmission = _activeFocusWriteAdmission;
        Action? previousRejection = _rejectActiveFocusWrite;
        UIFocusWriteReceipt? previousAcquisition = _activeFocusAcquisition;
        _activeFocusAcquisition = receipt;
        _activeFocusWriteAdmission = Current;
        _rejectActiveFocusWrite = () => rejected = true;
        try
        {
            try
            {
                SetFieldValidated(ref _isFocused, value, (Action)Require,
                    (require, original, current, _) =>
                    {
                        require();
                        if (original != current)
                        {
                            rejected = true;
                            throw new InvalidOperationException("Focus changed during assignment.");
                        }
                    }, propertyName: nameof(IsFocused));
                return Current() && _isFocused == value;
            }
            catch (InvalidOperationException) when (rejected) { return false; }
        }
        finally
        {
            if (receipt is not null && receipt.OwnerCurrent && _isFocused)
                receipt.GainWasWritten = true;
            _activeFocusWriteAdmission = previousAdmission;
            _rejectActiveFocusWrite = previousRejection;
            _activeFocusAcquisition = previousAcquisition;
        }
    }

    /// <summary>Releases the captured focus without committing, restoring, or formatting its draft.</summary>
    public bool TryReleaseCapturedFocus(UIFocusWriteReceipt receipt,
        Func<bool> isCurrent, Action retireHostInput)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(retireHostInput);
        if (!ReferenceEquals(receipt.Target, this) || receipt.AbortStarted || !receipt.OwnsFocus)
            return receipt.AbortCompleted;
        if (!isCurrent() || !receipt.OwnsFocus)
            return false;
        receipt.AbortStarted = true;
        AdvanceFocusWriteIntent();
        receipt.AdvanceAttempt(_focusWriteIntentRevision);
        receipt.RetireHostInput = retireHostInput;
        bool rejected = false;
        bool Current() => !rejected && receipt.OwnerCurrent && isCurrent() &&
            !rejected && receipt.OwnerCurrent;
        void Require()
        {
            if (Current()) return;
            rejected = true;
            throw new InvalidOperationException("The captured focus owner changed during release.");
        }
        Func<bool>? previousAdmission = _activeFocusWriteAdmission;
        Action? previousRejection = _rejectActiveFocusWrite;
        UIFocusWriteReceipt? previousAbort = _activeFocusAbort;
        _activeFocusWriteAdmission = Current;
        _rejectActiveFocusWrite = () => rejected = true;
        _activeFocusAbort = receipt;
        try
        {
            try
            {
                bool changed = SetFieldValidated(ref _isFocused, false, (Action)Require,
                    (require, original, current, _) =>
                    {
                        require();
                        if (original != current)
                        {
                            rejected = true;
                            throw new InvalidOperationException("Focus changed during release.");
                        }
                    }, propertyName: nameof(IsFocused));
                if (!Current()) return false;
                receipt.AbortVetoed = !changed && _isFocused && !receipt.AbortLossStarted;
                receipt.AbortCompleted = !_isFocused;
                if (receipt.CleanupFailure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                return receipt.AbortCompleted;
            }
            catch (InvalidOperationException) when (rejected)
            {
                if (receipt.CleanupFailure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                return false;
            }
            catch when (receipt.CleanupFailure is { } cleanupFailure)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                throw;
            }
        }
        finally
        {
            if (receipt.OwnerCurrent && !_isFocused)
                receipt.AbortCompleted = true;
            receipt.RetireHostInput = null;
            _activeFocusWriteAdmission = previousAdmission;
            _rejectActiveFocusWrite = previousRejection;
            _activeFocusAbort = previousAbort;
        }
    }

    /// <summary>Ends captured focus effects under the original owner proof.</summary>
    protected virtual void OnCapturedFocusReleased(UIFocusWriteReceipt receipt)
    {
        Func<bool>? admission = FocusWriteAdmission;
        if (!IsFocusWriteCurrent(admission)) return;
        receipt.RetireHostInput?.Invoke();
        if (!IsFocusWriteCurrent(admission)) return;
        receipt.LostFocusRaised = true;
        OnLostFocus();
    }
}

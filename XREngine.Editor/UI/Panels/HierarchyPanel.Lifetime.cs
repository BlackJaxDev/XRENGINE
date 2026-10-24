using System;
using System.Runtime.ExceptionServices;
using XREngine.Components;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

public partial class HierarchyPanel
{
    private bool _hierarchyStopped;
    private bool _hierarchyDestroying;
    private bool _releasingHierarchy;
    private bool _activatingHierarchy;
    private bool _selectionSubscribed;
    private bool _activationPending;
    private bool _remakingHierarchy;
    private bool _remakePending;
    private bool _hierarchyWorkTickRegistered;
    private uint _hierarchyRevision;
    private IUICanvasInputSource? _rightClickInput;
    private Action<UIInteractableComponent>? _rightClickCallback;

    private bool CanUseHierarchy => !_hierarchyStopped && !_hierarchyDestroying &&
        !IsDestroyed && !IsDestroyQueued;

    protected override void OnComponentActivated()
    {
        if (_hierarchyDestroying || IsDestroyed || IsDestroyQueued)
            return;
        if (_releasingHierarchy)
        {
            _activationPending = true;
            return;
        }
        if (_activatingHierarchy)
        {
            if (_hierarchyStopped)
            {
                _activationPending = true;
                ScheduleHierarchyWork();
            }
            return;
        }
        if (!_hierarchyStopped && _selectionSubscribed)
            return;
        _activatingHierarchy = true;
        try { ActivateHierarchy(); }
        finally { _activatingHierarchy = false; }
    }

    private void ActivateHierarchy()
    {
        _activationPending = false;
        _hierarchyStopped = false;
        uint revision = _hierarchyRevision;
        base.OnComponentActivated();
        if (!CanUseHierarchy || revision != _hierarchyRevision)
            return;
        RestoreHierarchyState();
        if (!_selectionSubscribed)
        {
            _selectionSubscribed = true;
            Selection.SelectionChanged += OnSelectionChanged;
        }
        SubscribeRightClick();
        if (!CanUseHierarchy || revision != _hierarchyRevision)
            return;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, PollVirtualTreeSources);
        RemakeChildren();
        if (CanUseHierarchy && revision == _hierarchyRevision)
            RestoreRenameFocus();
    }

    protected override void OnComponentDeactivated()
        => StopHierarchy(destroying: false);

    protected override void OnDestroying()
        => StopHierarchy(destroying: true);

    private void StopHierarchy(bool destroying)
    {
        bool wasDestroying = _hierarchyDestroying;
        _hierarchyDestroying |= destroying;
        _hierarchyStopped = true;
        _activationPending = false;
        _remakePending = false;
        _hierarchyRevision++;
        Exception? failure = null;
        if (_releasingHierarchy)
        {
            AttemptHierarchyRelease(CancelHierarchyWork, ref failure);
            if (destroying && !wasDestroying)
                AttemptHierarchyRelease(() => base.OnDestroying(), ref failure);
            ThrowHierarchyFailure(failure);
            return;
        }

        _releasingHierarchy = true;
        try
        {
            AttemptHierarchyRelease(CancelHierarchyWork, ref failure);
            AttemptHierarchyRelease(SaveHierarchyState, ref failure);
            // Stop the old tick owner before callbacks can request activation.
            if (destroying)
                AttemptHierarchyRelease(() => base.OnDestroying(), ref failure);
            else
                AttemptHierarchyRelease(() => base.OnComponentDeactivated(), ref failure);
            if (_selectionSubscribed)
            {
                _selectionSubscribed = false;
                Selection.SelectionChanged -= OnSelectionChanged;
            }
            IUICanvasInputSource? input = _rightClickInput;
            AttemptHierarchyRelease(UnsubscribeRightClick, ref failure);
            AttemptHierarchyRelease(() => UnregisterTick(ETickGroup.Late, ETickOrder.Scene, PollVirtualTreeSources), ref failure);
            AttemptHierarchyRelease(() => ReleaseVirtualTree(input), ref failure);
            AttemptHierarchyRelease(ReleaseHierarchyMenu, ref failure);
            _pendingDragStarts.Clear();
            _lastClickedNode = null;
            _lastClickTicks = long.MinValue;
            _nodePendingRename = null;
            _contextMenuTargetNode = null;
        }
        finally
        {
            _releasingHierarchy = false;
            if (!_hierarchyDestroying && _activationPending)
                AttemptHierarchyRelease(ScheduleHierarchyWork, ref failure);
        }
        ThrowHierarchyFailure(failure);
    }

    private void ScheduleHierarchyWork()
    {
        if (_hierarchyWorkTickRegistered || _hierarchyDestroying || IsDestroyed || IsDestroyQueued)
            return;
        _hierarchyWorkTickRegistered = true;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, CompleteDeferredHierarchyWork);
    }

    private void CancelHierarchyWork()
    {
        _hierarchyWorkTickRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CompleteDeferredHierarchyWork);
    }

    private void CompleteDeferredHierarchyWork()
    {
        CancelHierarchyWork();
        if (_hierarchyDestroying || IsDestroyed || IsDestroyQueued)
            return;
        if (_releasingHierarchy || _remakingHierarchy || _activatingHierarchy)
        {
            ScheduleHierarchyWork();
            return;
        }
        if (_activationPending)
            OnComponentActivated();
        else if (_remakePending && CanUseHierarchy)
            RemakeChildren();
    }

    private static void AttemptHierarchyRelease(Action action, ref Exception? failure)
    {
        try { action(); }
        catch (Exception ex) { failure ??= ex; }
    }

    private static void ThrowHierarchyFailure(Exception? failure)
    {
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

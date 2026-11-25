using XREngine.Components;

namespace XREngine.Rendering.UI;

public sealed partial class UITabStripComponent
{
    private const int MaximumFocusRestoreTicks = 120;
    private IUICanvasInputSource? _pendingTabInput;
    private UICanvasComponent? _pendingTabCanvas;
    private UITabButtonComponent? _pendingTabPreviousButton;
    private ulong _pendingTabId;
    private ulong _pendingFocusIntentRevision;
    private ulong _pendingTabsRevision;
    private ulong _pendingFocusRequestRevision;
    private int _pendingFocusTicks;
    private bool _focusRestoreTickRegistered;

    private (IUICanvasInputSource? Input, UICanvasComponent? Canvas,
        UITabButtonComponent? PreviousButton, ulong Id, ulong IntentRevision) CaptureTabFocusRequest()
    {
        UICanvasComponent? canvas = UserInterfaceCanvas;
        IUICanvasInputSource? input = canvas?.GetInputComponent();
        if (input is null || input.FocusIntentRevision == 0 || input.HasPendingFocusRequest)
        {
            CancelTabFocusRestore();
            return default;
        }
        if (_pendingTabId != 0)
        {
            if (ReferenceEquals(_pendingTabInput, input) && ReferenceEquals(_pendingTabCanvas, canvas) &&
                _pendingFocusIntentRevision == input.FocusIntentRevision)
                return (input, canvas, _pendingTabPreviousButton, _pendingTabId,
                    _pendingFocusIntentRevision);
            CancelTabFocusRestore();
        }
        for (int index = 0; index < _count; index++)
        {
            Slot slot = _slots[index];
            if (slot.Id != 0 && slot.Button.IsFocused &&
                ReferenceEquals(input.FocusedComponent, slot.Button))
                return (input, canvas, slot.Button, slot.Id, input.FocusIntentRevision);
        }
        return default;
    }

    private void RestoreTabFocusAfterUpdate((IUICanvasInputSource? Input, UICanvasComponent? Canvas,
        UITabButtonComponent? PreviousButton, ulong Id, ulong IntentRevision) request,
        ulong acceptedTabsRevision)
    {
        if (request.Input is not { } input || request.Canvas is not { } canvas || request.Id == 0 ||
            FindIndex(request.Id) < 0 || !ReferenceEquals(UserInterfaceCanvas, canvas) ||
            !ReferenceEquals(canvas.GetInputComponent(), input) || input.HasPendingFocusRequest ||
            input.FocusIntentRevision != request.IntentRevision)
        {
            CancelTabFocusRestore();
            return;
        }
        Slot slot = _slots[FindIndex(request.Id)];
        if (ReferenceEquals(slot.Button, request.PreviousButton) && slot.Button.IsFocused &&
            ReferenceEquals(input.FocusedComponent, slot.Button))
        {
            CancelTabFocusRestore();
            return;
        }
        _pendingTabInput = input;
        _pendingTabCanvas = canvas;
        _pendingTabPreviousButton = request.PreviousButton;
        _pendingTabId = request.Id;
        _pendingFocusIntentRevision = request.IntentRevision;
        _pendingTabsRevision = acceptedTabsRevision;
        _pendingFocusTicks = 0;
        _pendingFocusRequestRevision++;
        if (_focusRestoreTickRegistered)
            return;
        _focusRestoreTickRegistered = true;
        try { RegisterTick(ETickGroup.Late, ETickOrder.Scene, RestoreTabFocusTick); }
        catch
        {
            CancelTabFocusRestore();
            throw;
        }
    }

    private void RestoreTabFocusTick()
    {
        IUICanvasInputSource? input = _pendingTabInput;
        UICanvasComponent? canvas = _pendingTabCanvas;
        ulong id = _pendingTabId;
        ulong revision = _pendingFocusRequestRevision;
        if (input is null || canvas is null || id == 0 || !IsActiveInHierarchy || IsDestroyed ||
            IsDestroyQueued || !ReferenceEquals(UserInterfaceCanvas, canvas) ||
            !ReferenceEquals(canvas.GetInputComponent(), input) || input.HasPendingFocusRequest ||
            input.FocusIntentRevision != _pendingFocusIntentRevision ||
            _pendingTabsRevision != _acceptedTabsRevision || _settingTabs ||
            ++_pendingFocusTicks > MaximumFocusRestoreTicks)
        {
            CancelTabFocusRestore();
            return;
        }
        int index = FindIndex(id);
        if (index < 0)
        {
            CancelTabFocusRestore();
            return;
        }
        UITabButtonComponent target = _slots[index].Button;
        if (!target.CanInteract || input.FocusedComponent is { } focused &&
            !ReferenceEquals(focused, _pendingTabPreviousButton) && !ReferenceEquals(focused, target))
        {
            CancelTabFocusRestore();
            return;
        }
        if (input.TryFocusPresented(target) && revision == _pendingFocusRequestRevision &&
            ReferenceEquals(_pendingTabInput, input) && _pendingTabId == id)
            CancelTabFocusRestore();
    }

    private void CancelTabFocusRestore()
    {
        _pendingFocusRequestRevision++;
        _pendingTabInput = null;
        _pendingTabCanvas = null;
        _pendingTabPreviousButton = null;
        _pendingTabId = 0;
        _pendingFocusIntentRevision = 0;
        _pendingTabsRevision = 0;
        _pendingFocusTicks = 0;
        if (!_focusRestoreTickRegistered)
            return;
        _focusRestoreTickRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RestoreTabFocusTick);
    }

    protected override void OnComponentDeactivated()
    {
        CancelTabFocusRestore();
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        CancelTabFocusRestore();
        TabActivated = null;
        CloseRequested = null;
        MoveRequested = null;
        base.OnDestroying();
    }
}

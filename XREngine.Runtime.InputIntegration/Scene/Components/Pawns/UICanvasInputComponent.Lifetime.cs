using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    private static void TryInputCleanup(Action release, ref List<Exception>? failures)
    {
        try { release(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
    }

    private static void ThrowInputCleanupFailures(List<Exception>? failures)
    {
        if (failures is null)
            return;
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("UI input cleanup failed.", failures);
    }

    /// <summary>Releases the exact owners captured during this activation.</summary>
    private void TeardownInputOwners()
    {
        if (_inputCleanupActive)
            return;
        _inputCleanupActive = true;
        _inputDeactivating = true;
        _inputLifecycleRevision++;

        IRuntimeRenderSchedulingServices? host = _viewportSubscriptionHost;
        bool collectSubscribed = _subscribedToCollect;
        bool swapSubscribed = _subscribedToSwap;
        _viewportSubscriptionHost = null;
        _subscribedToCollect = false;
        _subscribedToSwap = false;
        UIPublication? publication = _inputPublication;
        _inputPublication = null;
        _inputCanvas = null;
        _inputOutput = null;
        IRuntimeInputControllablePawn? pawn = _observedPawn;
        _observedPawn = null;
        InputInterface? linkedInput = _linkedInput;
        _linkedInput = null;
        UIInteractableComponent? focused = _linkedFocusedComponent;
        _linkedFocusedComponent = null;
        SetField(ref _focusedComponent, null, publishNotifications: false);
        ClearPendingVirtualFocus();
        List<Exception>? failures = null;
        try
        {
            IPawnController? controller = null;
            XRViewport? imeViewport = null;
            TryInputCleanup(() => controller = _owningPawn?.Controller, ref failures);
            TryInputCleanup(() => imeViewport = _owningPawn?.Viewport as XRViewport, ref failures);
            TryInputCleanup(() => ClearPointerPress(), ref failures);
            TryInputCleanup(RetireImeSession, ref failures);
            RetireFocusedInput(focused, controller, ref failures);
            TryInputCleanup(() => imeViewport?.Window?.TrySetImeFocusTarget(default, 0), ref failures);
            RetirePawnInput(pawn, linkedInput, ref failures);
            while (_registeredInputs.Count > 0)
            {
                InputInterface? input = null;
                foreach (InputInterface registered in _registeredInputs)
                {
                    input = registered;
                    break;
                }
                if (input is null)
                    break;
                _registeredInputs.Remove(input);
                TryInputCleanup(() =>
                {
                    bool previous = input.Unregister;
                    try
                    {
                        input.Unregister = true;
                        RegisterInputMappings(input);
                    }
                    finally { input.Unregister = previous; }
                }, ref failures);
            }
            for (int index = 0; index < LastUIElementIntersections.Count; index++)
                if (LastUIElementIntersections[index].Owner is UIInteractableComponent target)
                {
                    TryInputCleanup(() => target.IsMouseOver = false, ref failures);
                    TryInputCleanup(() => target.IsMouseDirectlyOver = false, ref failures);
                }
            LastUIElementIntersections.Clear();
            UIElementIntersections.Clear();
            _lastIntersectionIdentities.Clear();
            _currentIntersectionIdentities.Clear();
            _inputRecords.Clear();
            _focusReturns.Clear();
            _queriedCanvasLifetime = 0;
            _queriedHitGeneration = 0;
            _ctrlLeft = _ctrlRight = _shiftLeft = _shiftRight = _altLeft = _altRight = false;
            IsCtrlHeld = IsShiftHeld = IsAltHeld = false;
            TopMostElement = null;
            TopMostInteractable = null;
            TryInputCleanup(() => publication?.Release(), ref failures);
            TryInputCleanup(() => SynchronizeFocusScope(null), ref failures);
            if (host is not null)
            {
                if (collectSubscribed)
                    TryInputCleanup(() => host.UnsubscribeViewportCollectVisible(CollectVisible), ref failures);
                if (swapSubscribed)
                    TryInputCleanup(() => host.UnsubscribeViewportSwapBuffers(SwapBuffers), ref failures);
            }
        }
        finally { _inputCleanupActive = false; }
        ThrowInputCleanupFailures(failures);
    }

    private void RetireFocusedInput(UIInteractableComponent? target, IPawnController? controller,
        ref List<Exception>? failures)
    {
        while (_focusedInputTargets.Count > 0)
        {
            InputInterface? input = null;
            UIInteractableComponent? registeredTarget = null;
            foreach (var pair in _focusedInputTargets)
            {
                input = pair.Key;
                registeredTarget = pair.Value;
                break;
            }
            if (input is null || registeredTarget is null)
                break;
            _focusedInputTargets.Remove(input);
            TryInputCleanup(() => InvokeFocusInput(input, registeredTarget, true), ref failures);
        }
        if (target is not null)
        {
            TryInputCleanup(() => target.PropertyChanged -= FocusedComponentPropertyChanged, ref failures);
            TryInputCleanup(() => target.IsFocused = false, ref failures);
        }
        if (controller is not null)
            TryInputCleanup(() =>
            {
                if (ReferenceEquals(controller.FocusedInteractable, _controllerFocusedTarget))
                    controller.FocusedInteractable = null;
            }, ref failures);
        _controllerFocusedTarget = null;
    }

    private void RetirePawnInput(IRuntimeInputControllablePawn? pawn, InputInterface? input,
        ref List<Exception>? failures)
    {
        if (pawn is not null)
        {
            TryInputCleanup(() => pawn.PropertyChanging -= OwningPawnPropertyChanging, ref failures);
            TryInputCleanup(() => pawn.PropertyChanged -= OwningPawnPropertyChanged, ref failures);
            TryInputCleanup(() => pawn.LinkedUiInputComponents.Remove(this), ref failures);
        }
        if (input is null)
            return;
        TryInputCleanup(input.TryUnregisterInput, ref failures);
        TryInputCleanup(() => input.InputRegistration -= RegisterInput, ref failures);
        TryInputCleanup(input.TryRegisterInput, ref failures);
    }
}

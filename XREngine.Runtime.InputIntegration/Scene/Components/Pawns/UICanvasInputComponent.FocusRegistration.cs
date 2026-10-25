using XREngine.Input.Devices;
using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    private UIInteractableComponent? _linkedFocusedComponent;
    private readonly Dictionary<InputInterface, UIInteractableComponent> _focusedInputTargets =
        new(ReferenceEqualityComparer.Instance);
    private bool _reconcilingFocus;
    private UIInteractableComponent? _controllerFocusedTarget;

    /// <summary>
    /// Applies the latest requested focus after each user callback. Input mappings
    /// belong to a concrete target, so a later focus change cannot leave old handlers.
    /// </summary>
    private void ReconcileFocusedComponent()
    {
        if (_reconcilingFocus || _inputCleanupActive)
            return;
        _reconcilingFocus = true;
        try
        {
            while (true)
            {
                if (!GuardedFocusAdmissionCurrent()) return;
                UIInteractableComponent? desired = FocusedComponent;
                if (!ReferenceEquals(_linkedFocusedComponent, desired))
                {
                    UIInteractableComponent? previous = _linkedFocusedComponent;
                    _linkedFocusedComponent = null;
                    if (previous is not null)
                    {
                        previous.PropertyChanged -= FocusedComponentPropertyChanged;
                        if (_activeGuardedFocusAdmission is { } previousAdmission)
                        {
                            if (!previous.SetFocusGuarded(false, previousAdmission))
                            {
                                _guardedFocusOperationFailed = true;
                                _linkedFocusedComponent = previous;
                                previous.PropertyChanged += FocusedComponentPropertyChanged;
                                return;
                            }
                            if (!GuardedFocusAdmissionCurrent()) return;
                            while (RemoveStaleFocusInput(null))
                                if (!GuardedFocusAdmissionCurrent()) return;
                        }
                        else
                        {
                            while (RemoveStaleFocusInput(null)) { }
                            previous.IsFocused = false;
                        }
                        if (!GuardedFocusAdmissionCurrent()) return;
                    }
                    if (!ReferenceEquals(FocusedComponent, desired))
                        continue;
                    _linkedFocusedComponent = desired;
                    if (desired is not null)
                    {
                        desired.PropertyChanged += FocusedComponentPropertyChanged;
                        if (_activeGuardedFocusAdmission is { } desiredAdmission)
                        {
                            if (!desired.SetFocusGuarded(true, desiredAdmission))
                            {
                                _guardedFocusOperationFailed = true;
                                return;
                            }
                        }
                        else
                            desired.IsFocused = true;
                        if (!GuardedFocusAdmissionCurrent()) return;
                        continue;
                    }
                }

                UIInteractableComponent? inputTarget = desired is { RegisterInputsOnFocus: true }
                    ? desired : null;
                if (RemoveStaleFocusInput(inputTarget) || AddMissingFocusInput(inputTarget))
                {
                    if (!GuardedFocusAdmissionCurrent()) return;
                    continue;
                }

                var controller = _owningPawn?.Controller;
                if (controller is not null && !ReferenceEquals(controller.FocusedInteractable, desired) &&
                    (desired is not null || ReferenceEquals(controller.FocusedInteractable, _controllerFocusedTarget)))
                    controller.FocusedInteractable = desired;
                if (!GuardedFocusAdmissionCurrent()) return;
                _controllerFocusedTarget = desired;
                if (ReferenceEquals(FocusedComponent, desired))
                    break;
            }
        }
        finally
        {
            _reconcilingFocus = false;
            UpdateImeFocusTarget();
        }
    }

    private bool RemoveStaleFocusInput(UIInteractableComponent? desired)
    {
        InputInterface? staleInput = null;
        UIInteractableComponent? staleTarget = null;
        foreach (var pair in _focusedInputTargets)
            if (!ReferenceEquals(pair.Value, desired) || !_registeredInputs.Contains(pair.Key))
            {
                staleInput = pair.Key;
                staleTarget = pair.Value;
                break;
            }
        if (staleInput is null || staleTarget is null)
            return false;
        // End enumeration and remove ownership before a callback can re-enter.
        _focusedInputTargets.Remove(staleInput);
        InvokeFocusInput(staleInput, staleTarget, true);
        return true;
    }

    private bool AddMissingFocusInput(UIInteractableComponent? desired)
    {
        if (desired is null)
            return false;
        InputInterface? missingInput = null;
        foreach (InputInterface input in _registeredInputs)
            if (!_focusedInputTargets.ContainsKey(input))
            {
                missingInput = input;
                break;
            }
        if (missingInput is null)
            return false;
        _focusedInputTargets.Add(missingInput, desired);
        InvokeFocusInput(missingInput, desired, false);
        return true;
    }

    private static void InvokeFocusInput(InputInterface input, UIInteractableComponent target, bool unregister)
    {
        bool previous = input.Unregister;
        try
        {
            input.Unregister = unregister;
            target.RegisterInput(input);
        }
        finally { input.Unregister = previous; }
    }

    private void UnregisterCanvasInputs()
    {
        while (_registeredInputs.Count > 0)
        {
            InputInterface? selected = null;
            foreach (InputInterface input in _registeredInputs)
            {
                selected = input;
                break;
            }
            if (selected is null)
                break;
            bool previous = selected.Unregister;
            try
            {
                selected.Unregister = true;
                RegisterInput(selected);
            }
            finally { selected.Unregister = previous; }
        }
    }
}

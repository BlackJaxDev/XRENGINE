using System;
using System.Collections.Generic;
using System.Diagnostics;
using XREngine.Core;
using XREngine.Core.Attributes;
using XREngine.Rendering.UI;
using XREngine.Scene;
using YamlDotNet.Serialization;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    private const float MaxGamepadRepeatDeltaSeconds = 0.05f;
    private static readonly int GamepadNavigationTickOrder = (int)ETickOrder.Input + 2;

    [RuntimeOnly, YamlIgnore]
    private readonly UINavigationRepeater _navigationRepeater = new();
    [RuntimeOnly, YamlIgnore]
    private readonly List<UIInteractableComponent> _navigationCandidateScratch = new(32);
    [RuntimeOnly, YamlIgnore]
    private readonly UINavigationFlowMap _fallbackFlowMap = new();
    [RuntimeOnly, YamlIgnore]
    private UIInteractableComponent? _gamepadPressedTarget;
    [RuntimeOnly, YamlIgnore]
    private bool _gamepadBackPending;
    [RuntimeOnly, YamlIgnore]
    private bool _suppressMouseHoverUntilMove;
    [RuntimeOnly, YamlIgnore]
    private bool _gamepadTickRegistered;
    [RuntimeOnly, YamlIgnore]
    private long _lastGamepadTickTimestamp;

    private UINavigationFlowMap? _activeNavigationFlowMap;
    /// <summary>
    /// Optional active <see cref="UINavigationFlowMap"/> overriding auto-discovered menu flow maps
    /// (for example while a modal pause menu is open).
    /// </summary>
    [RuntimeOnly, YamlIgnore]
    public UINavigationFlowMap? ActiveNavigationFlowMap
    {
        get => _activeNavigationFlowMap;
        set => SetField(ref _activeNavigationFlowMap, value);
    }

    /// <summary>
    /// Moves focus and gamepad selection highlight to <paramref name="target"/> and suppresses
    /// stationary mouse hover until the pointer moves.
    /// </summary>
    public void FocusWithGamepad(UIInteractableComponent? target)
    {
        if (target is not null && !UINavigationFlowMap.IsEligible(target))
            return;

        _suppressMouseHoverUntilMove = target is not null;
        if (target is not null && TopMostInteractable is { IsDestroyed: false } hovered && !ReferenceEquals(hovered, target))
            hovered.IsMouseDirectlyOver = false;

        UIInteractableComponent? previous = FocusedComponent;
        if (previous is not null && !ReferenceEquals(previous, target))
        {
            if (previous is UIButtonComponent prevButton)
                prevButton.SetPressed(false);
            if (previous.IsGamepadSelected)
                previous.IsGamepadSelected = false;
        }

        FocusedComponent = target;
        if (target is not null && !target.IsGamepadSelected)
            target.IsGamepadSelected = true;
    }

    /// <summary>
    /// Clears held D-pad, analog stick, press-target, and hover-suppression state.
    /// </summary>
    public void ResetGamepadNavigationState()
    {
        ClearGamepadPress(restoreVisual: true);
        _gamepadBackPending = false;
        _suppressMouseHoverUntilMove = false;
        _navigationRepeater.Reset();
        _lastGamepadTickTimestamp = 0;

        if (FocusedComponent is { IsGamepadSelected: true } focused)
            focused.IsGamepadSelected = false;
    }

    /// <summary>
    /// Navigates focus one step along <paramref name="direction"/> using the active flow map.
    /// Returns true when focus moved or initialized onto an eligible interactable.
    /// </summary>
    public bool NavigateDirection(EUINavigationDirection direction)
    {
        if (direction == EUINavigationDirection.None)
            return false;

        if (!TryResolveActiveFlowMap(out UINavigationFlowMap? flowMap, out SceneNode? scopeRoot) || flowMap is null)
            return false;

        List<UIInteractableComponent>? candidates = null;
        if (flowMap.AllowSpatialFallback || flowMap.WrapVertical || flowMap.WrapHorizontal)
        {
            CollectEligibleCandidates(scopeRoot, _navigationCandidateScratch);
            candidates = _navigationCandidateScratch;
        }

        UIInteractableComponent? next = flowMap.ResolveNext(FocusedComponent, direction, candidates);
        _navigationCandidateScratch.Clear();
        if (next is null)
            return false;

        FocusWithGamepad(next);
        return true;
    }

    private void RegisterGamepadNavigationTick()
    {
        if (_gamepadTickRegistered)
            return;

        _lastGamepadTickTimestamp = Stopwatch.GetTimestamp();
        RegisterTick(ETickGroup.Normal, GamepadNavigationTickOrder, TickGamepadNavigation);
        _gamepadTickRegistered = true;
    }

    private void UnregisterGamepadNavigationTick()
    {
        if (!_gamepadTickRegistered)
            return;

        UnregisterTick(ETickGroup.Normal, GamepadNavigationTickOrder, TickGamepadNavigation);
        _gamepadTickRegistered = false;
        _lastGamepadTickTimestamp = 0;
    }

    private void TickGamepadNavigation()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _lastGamepadTickTimestamp != 0
            ? (float)((now - _lastGamepadTickTimestamp) / (double)Stopwatch.Frequency)
            : 0.0f;
        _lastGamepadTickTimestamp = now;

        if (dt <= 0.0f)
            return;
        if (dt > MaxGamepadRepeatDeltaSeconds)
            dt = MaxGamepadRepeatDeltaSeconds;

        if (!TryResolveActiveFlowMap(out UINavigationFlowMap? flowMap, out _) || flowMap is null)
            return;

        UINavigationRepeatSettings settings = flowMap.RepeatSettings;
        EUINavigationDirection step = _navigationRepeater.Tick(dt, flowMap.AllowedDirections, in settings);
        if (step != EUINavigationDirection.None)
            NavigateDirection(step);
    }

    private void OnGamepadStartButtonDown()
    {
        if (IsVirtualGamepadDispatch)
            return;

        GamepadStartPressed?.Invoke();
    }

    private void OnDPadUpStateChanged(bool pressed)
    {
        if (IsVirtualGamepadDispatch)
            return;

        if (pressed)
            OnDPadUp();
        UpdateDPadNavigation(EUINavigationDirection.Up, pressed);
    }

    private void OnDPadDownStateChanged(bool pressed)
    {
        if (IsVirtualGamepadDispatch)
            return;

        if (pressed)
            OnDPadDown();
        UpdateDPadNavigation(EUINavigationDirection.Down, pressed);
    }

    private void OnDPadLeftStateChanged(bool pressed)
    {
        if (IsVirtualGamepadDispatch)
            return;

        if (pressed)
            OnDPadLeft();
        UpdateDPadNavigation(EUINavigationDirection.Left, pressed);
    }

    private void OnDPadRightStateChanged(bool pressed)
    {
        if (IsVirtualGamepadDispatch)
            return;

        if (pressed)
            OnDPadRight();
        UpdateDPadNavigation(EUINavigationDirection.Right, pressed);
    }

    private void UpdateDPadNavigation(EUINavigationDirection direction, bool pressed)
    {
        _navigationRepeater.SetDPadState(direction, pressed);
        EvaluateImmediateGamepadNavigation();
    }

    private void HandleLeftStickX(float value)
    {
        if (IsVirtualGamepadDispatch)
            return;

        _navigationRepeater.SetStickX(value);
        EvaluateImmediateGamepadNavigation();
    }

    private void HandleLeftStickY(float value)
    {
        if (IsVirtualGamepadDispatch)
            return;

        _navigationRepeater.SetStickY(value);
        EvaluateImmediateGamepadNavigation();
    }

    private void EvaluateImmediateGamepadNavigation()
    {
        if (!TryResolveActiveFlowMap(out UINavigationFlowMap? flowMap, out _) || flowMap is null)
            return;

        UINavigationRepeatSettings settings = flowMap.RepeatSettings;
        EUINavigationDirection step = _navigationRepeater.EvaluateImmediate(flowMap.AllowedDirections, in settings);
        if (step != EUINavigationDirection.None)
            NavigateDirection(step);
    }

    private void HandleGamepadInteractButtonDown()
    {
        if (_inputDeactivating || _inputTerminal || IsVirtualGamepadDispatch)
            return;

        ClearGamepadPress(restoreVisual: true);
        UIInteractableComponent? target = FocusedComponent;
        if (target is null || !IsPointerTargetEligible(target))
            return;

        _gamepadPressedTarget = target;
        if (target is UIButtonComponent button)
            button.SetPressed(true);

        if (target.InteractOnButtonDown)
            target.OnInteract();
    }

    private void HandleGamepadInteractButtonUp()
    {
        if (_inputDeactivating || _inputTerminal || IsVirtualGamepadDispatch)
            return;

        UIInteractableComponent? pressed = _gamepadPressedTarget;
        ClearGamepadPress(restoreVisual: true);
        if (pressed is null || !ReferenceEquals(FocusedComponent, pressed) || !IsPointerTargetEligible(pressed))
            return;

        if (!pressed.InteractOnButtonDown)
            pressed.OnInteract();
    }

    private void HandleGamepadBackButtonDown()
    {
        if (_inputDeactivating || _inputTerminal || IsVirtualGamepadDispatch)
            return;

        if (FocusedComponent is not null || GamepadBackPressed is not null)
            _gamepadBackPending = true;
    }

    private void HandleGamepadBackButtonUp()
    {
        if (_inputDeactivating || _inputTerminal || IsVirtualGamepadDispatch || !_gamepadBackPending)
            return;

        _gamepadBackPending = false;
        UIInteractableComponent? target = FocusedComponent;
        if (target is not null && IsPointerTargetEligible(target))
            BackPressed?.Invoke(target);
        if (target is not null && ReferenceEquals(FocusedComponent, target) && IsPointerTargetEligible(target))
            target.OnBack();
        GamepadBackPressed?.Invoke();
    }

    private void ClearGamepadPress(bool restoreVisual)
    {
        if (restoreVisual && _gamepadPressedTarget is UIButtonComponent { IsDestroyed: false } button)
            button.SetPressed(false);

        _gamepadPressedTarget = null;
    }

    private void ClearGamepadSelectionForPointerHover(UIInteractableComponent hovered)
    {
        if (FocusedComponent is { IsDestroyed: false, IsGamepadSelected: true } focused && !ReferenceEquals(focused, hovered))
            focused.IsGamepadSelected = false;
    }

    private bool TryResolveActiveFlowMap(out UINavigationFlowMap? flowMap, out SceneNode? scopeRoot)
    {
        if (_activeNavigationFlowMap is not null)
        {
            flowMap = _activeNavigationFlowMap;
            scopeRoot = GetCameraCanvas()?.SceneNode;
            return true;
        }

        if (FocusedComponent is { IsDestroyed: false } focused)
        {
            if (FindAncestorFlowMapComponent(focused.SceneNode) is { } ancestorMap)
            {
                flowMap = ancestorMap.FlowMap;
                scopeRoot = ancestorMap.EffectiveScopeRoot;
                return true;
            }

            flowMap = _fallbackFlowMap;
            scopeRoot = GetCameraCanvas()?.SceneNode;
            return true;
        }

        if (GetCameraCanvas()?.SceneNode is { IsDestroyed: false } canvasNode
            && canvasNode.GetComponent<UINavigationFlowMapComponent>() is { IsDestroyed: false, IsActiveInHierarchy: true } canvasMap)
        {
            flowMap = canvasMap.FlowMap;
            scopeRoot = canvasMap.EffectiveScopeRoot;
            return true;
        }

        flowMap = null;
        scopeRoot = null;
        return false;
    }

    private static UINavigationFlowMapComponent? FindAncestorFlowMapComponent(SceneNode? node)
    {
        for (SceneNode? current = node; current is not null && !current.IsDestroyed; current = current.Parent)
        {
            if (current.GetComponent<UINavigationFlowMapComponent>() is { IsDestroyed: false, IsActiveInHierarchy: true } map)
                return map;
        }

        return null;
    }

    private static void CollectEligibleCandidates(SceneNode? root, List<UIInteractableComponent> output)
    {
        output.Clear();
        if (root is null || root.IsDestroyed)
            return;

        CollectEligibleCandidatesRecursive(root, output);
    }

    private static void CollectEligibleCandidatesRecursive(SceneNode node, List<UIInteractableComponent> output)
    {
        if (node.IsDestroyed || !node.IsActiveSelf)
            return;

        if (node.Transform is UIBoundableTransform boundable && boundable.Visibility != EVisibility.Visible)
            return;

        for (int i = 0; i < node.Components.Count; i++)
        {
            if (node.Components[i] is UIInteractableComponent interactable && UINavigationFlowMap.IsEligible(interactable))
                output.Add(interactable);
        }

        for (int i = 0; i < node.Transform.Children.Count; i++)
        {
            if (node.Transform.Children[i].SceneNode is { } child)
                CollectEligibleCandidatesRecursive(child, output);
        }
    }
}

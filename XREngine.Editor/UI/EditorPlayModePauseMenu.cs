using System.Globalization;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.VR;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>
/// Injects a generalized Apple-style native UI pause menu when the editor boots directly into play mode
/// running a game mode or pawn that does not specify a custom pause menu.
/// </summary>
internal static class EditorPlayModePauseMenu
{
    private const int OverlayRenderPass = (int)EDefaultRenderPass.OnTopForward;
    private const int BackdropZIndex = int.MaxValue - 80;
    private const int PanelZIndex = int.MaxValue - 70;
    private const int ButtonBackgroundZIndex = int.MaxValue - 60;
    private const int TextZIndex = int.MaxValue - 50;

    private const float PanelWidth = 360.0f;
    private const float PanelHeightBase = 434.0f;
    private const float PanelHeightWithCalibrate = 492.0f;
    private const float ButtonHeight = 46.0f;
    private const float CompactRowHeight = 42.0f;
    private const float StepperButtonWidth = 54.0f;
    private const float StepperSpacing = 8.0f;
    private const float DefaultBodyMeasurementMeters = 1.70f;
    private const float BodyMeasurementStepMeters = 0.01f;
    private const float MinBodyMeasurementMeters = 0.50f;
    private const float MaxPlayerHeightMeters = 2.75f;
    private const float MaxPlayerArmSpanMeters = 3.50f;
    private const string DefaultSubtitleText = "Press Esc or Start to resume";

    private static readonly ColorF4 TitleColor = new(1.0f, 1.0f, 1.0f, 0.96f);
    private static readonly ColorF4 SubtitleColor = new(0.84f, 0.87f, 0.94f, 0.72f);

    private static readonly UITransitionSpec OpenFadeTransition = UITransitionSpec.AppleEaseOut(0.22f);
    private static readonly UITransitionSpec OpenSpringTransition = UITransitionSpec.Spring(stiffness: 420.0f, damping: 32.0f);
    private static readonly UITransitionSpec CloseFadeTransition = UITransitionSpec.AppleEaseOut(0.18f);

    private static readonly UIStyleProperties ScrimClosedState = new()
    {
        BackgroundColor = new ColorF4(10.0f / 255.0f, 12.0f / 255.0f, 18.0f / 255.0f, 0.42f),
        DecodeSrgbColors = true,
        BackdropBlurRadius = 0.0f,
        BackdropBlurBlend = 0.0f,
        Opacity = 0.0f,
    };

    private static readonly UIStyleProperties ScrimOpenState = new()
    {
        BackgroundColor = new ColorF4(10.0f / 255.0f, 12.0f / 255.0f, 18.0f / 255.0f, 0.42f),
        DecodeSrgbColors = true,
        BackdropBlurRadius = 0.0f,
        BackdropBlurBlend = 0.0f,
        Opacity = 1.0f,
    };

    private static readonly UIStyleProperties CardClosedState = new()
    {
        BackgroundColor = new ColorF4(28.0f / 255.0f, 32.0f / 255.0f, 44.0f / 255.0f, 0.62f),
        GradientBottomColor = new ColorF4(18.0f / 255.0f, 21.0f / 255.0f, 30.0f / 255.0f, 0.72f),
        CornerRadii = new Vector4(22.0f, 22.0f, 22.0f, 22.0f),
        CornerExponent = 2.8f,
        BorderWidth = 1.0f,
        BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.16f),
        BorderHighlightBoost = 0.22f,
        ShadowColor = new ColorF4(0.0f, 0.0f, 0.0f, 0.52f),
        ShadowParams = new Vector4(0.0f, 18.0f, 36.0f, -6.0f),
        BackdropBlurRadius = 0.0f,
        BackdropBlurBlend = 0.0f,
        BackdropVibrancySaturation = 1.35f,
        BackdropVibrancyBrightness = 1.04f,
        DecodeSrgbColors = true,
        VisualScale = new Vector2(0.95f, 0.95f),
        VisualOffset = new Vector2(0.0f, -10.0f),
    };

    private static readonly UIStyleProperties CardOpenState = new()
    {
        BackgroundColor = new ColorF4(28.0f / 255.0f, 32.0f / 255.0f, 44.0f / 255.0f, 0.62f),
        GradientBottomColor = new ColorF4(18.0f / 255.0f, 21.0f / 255.0f, 30.0f / 255.0f, 0.72f),
        CornerRadii = new Vector4(22.0f, 22.0f, 22.0f, 22.0f),
        CornerExponent = 2.8f,
        BorderWidth = 1.0f,
        BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.16f),
        BorderHighlightBoost = 0.22f,
        ShadowColor = new ColorF4(0.0f, 0.0f, 0.0f, 0.52f),
        ShadowParams = new Vector4(0.0f, 18.0f, 36.0f, -6.0f),
        BackdropBlurRadius = 28.0f,
        BackdropBlurBlend = 1.0f,
        BackdropVibrancySaturation = 1.35f,
        BackdropVibrancyBrightness = 1.04f,
        DecodeSrgbColors = true,
        VisualScale = Vector2.One,
        VisualOffset = Vector2.Zero,
    };

    private static readonly UIStyle ResumeButtonStyle = new()
    {
        Base = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(14.0f / 255.0f, 138.0f / 255.0f, 1.0f, 0.92f),
            GradientBottomColor = new ColorF4(0.0f, 114.0f / 255.0f, 240.0f / 255.0f, 0.96f),
            TextColor = ColorF4.White,
            CornerRadii = new Vector4(12.0f, 12.0f, 12.0f, 12.0f),
            CornerExponent = 2.6f,
            BorderWidth = 1.0f,
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.24f),
            BorderHighlightBoost = 0.14f,
            DecodeSrgbColors = true,
            VisualScale = Vector2.One,
            VisualOffset = Vector2.Zero,
        },
        Hover = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(48.0f / 255.0f, 158.0f / 255.0f, 1.0f, 1.0f),
            GradientBottomColor = new ColorF4(16.0f / 255.0f, 134.0f / 255.0f, 1.0f, 1.0f),
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.38f),
            VisualScale = new Vector2(1.015f, 1.015f),
        },
        Focused = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(48.0f / 255.0f, 158.0f / 255.0f, 1.0f, 1.0f),
            GradientBottomColor = new ColorF4(16.0f / 255.0f, 134.0f / 255.0f, 1.0f, 1.0f),
            BorderWidth = 2.0f,
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.88f),
            VisualScale = new Vector2(1.02f, 1.02f),
        },
        Pressed = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(0.0f, 108.0f / 255.0f, 224.0f / 255.0f, 0.96f),
            GradientBottomColor = new ColorF4(0.0f, 92.0f / 255.0f, 200.0f / 255.0f, 0.98f),
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.18f),
            VisualScale = new Vector2(0.98f, 0.98f),
        },
        ColorTransition = UITransitionSpec.AppleEaseOut(0.12f),
        TransformTransition = UITransitionSpec.AppleEaseOut(0.12f),
    };

    private static readonly UIStyle CloseButtonStyle = new()
    {
        Base = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.12f),
            GradientBottomColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.08f),
            TextColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.92f),
            CornerRadii = new Vector4(12.0f, 12.0f, 12.0f, 12.0f),
            CornerExponent = 2.6f,
            BorderWidth = 1.0f,
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.14f),
            BorderHighlightBoost = 0.08f,
            DecodeSrgbColors = true,
            VisualScale = Vector2.One,
            VisualOffset = Vector2.Zero,
        },
        Hover = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(1.0f, 76.0f / 255.0f, 66.0f / 255.0f, 0.88f),
            GradientBottomColor = new ColorF4(225.0f / 255.0f, 52.0f / 255.0f, 44.0f / 255.0f, 0.92f),
            TextColor = ColorF4.White,
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.24f),
            VisualScale = new Vector2(1.015f, 1.015f),
        },
        Focused = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(1.0f, 76.0f / 255.0f, 66.0f / 255.0f, 0.92f),
            GradientBottomColor = new ColorF4(225.0f / 255.0f, 52.0f / 255.0f, 44.0f / 255.0f, 0.96f),
            TextColor = ColorF4.White,
            BorderWidth = 2.0f,
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.88f),
            VisualScale = new Vector2(1.02f, 1.02f),
        },
        Pressed = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(210.0f / 255.0f, 46.0f / 255.0f, 38.0f / 255.0f, 0.94f),
            GradientBottomColor = new ColorF4(184.0f / 255.0f, 36.0f / 255.0f, 30.0f / 255.0f, 0.96f),
            TextColor = ColorF4.White,
            VisualScale = new Vector2(0.98f, 0.98f),
        },
        ColorTransition = UITransitionSpec.AppleEaseOut(0.12f),
        TransformTransition = UITransitionSpec.AppleEaseOut(0.12f),
    };

    private static readonly UIStyle VrButtonStyle = new()
    {
        Base = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.14f),
            GradientBottomColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.09f),
            TextColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.94f),
            CornerRadii = new Vector4(12.0f, 12.0f, 12.0f, 12.0f),
            CornerExponent = 2.6f,
            BorderWidth = 1.0f,
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.18f),
            BorderHighlightBoost = 0.10f,
            DecodeSrgbColors = true,
            VisualScale = Vector2.One,
            VisualOffset = Vector2.Zero,
        },
        Hover = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(40.0f / 255.0f, 168.0f / 255.0f, 142.0f / 255.0f, 0.88f),
            GradientBottomColor = new ColorF4(24.0f / 255.0f, 140.0f / 255.0f, 118.0f / 255.0f, 0.92f),
            TextColor = ColorF4.White,
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.28f),
            VisualScale = new Vector2(1.015f, 1.015f),
        },
        Focused = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(40.0f / 255.0f, 168.0f / 255.0f, 142.0f / 255.0f, 0.92f),
            GradientBottomColor = new ColorF4(24.0f / 255.0f, 140.0f / 255.0f, 118.0f / 255.0f, 0.96f),
            TextColor = ColorF4.White,
            BorderWidth = 2.0f,
            BorderColor = new ColorF4(1.0f, 1.0f, 1.0f, 0.88f),
            VisualScale = new Vector2(1.02f, 1.02f),
        },
        Pressed = new UIStyleProperties
        {
            BackgroundColor = new ColorF4(18.0f / 255.0f, 126.0f / 255.0f, 106.0f / 255.0f, 0.96f),
            GradientBottomColor = new ColorF4(12.0f / 255.0f, 104.0f / 255.0f, 88.0f / 255.0f, 0.98f),
            TextColor = ColorF4.White,
            VisualScale = new Vector2(0.98f, 0.98f),
        },
        ColorTransition = UITransitionSpec.AppleEaseOut(0.12f),
        TransformTransition = UITransitionSpec.AppleEaseOut(0.12f),
    };

    private static UICanvasComponent? _attachedCanvas;
    private static SceneNode? _ownedCanvasNode;
    private static CameraComponent? _ownedCanvasCamera;
    private static SceneNode? _overlayRoot;
    private static UIBoundableTransform? _overlayTransform;
    private static UIBoundableTransform? _panelTransform;
    private static UIStyleAnimatorComponent? _scrimAnimator;
    private static UIStyleAnimatorComponent? _cardAnimator;
    private static UITextComponent? _subtitleLabel;
    private static UIButtonComponent? _resumeButton;
    private static UIButtonComponent? _openXrButton;
    private static UITextComponent? _openXrButtonLabel;
    private static UIButtonComponent? _calibrateVrButton;
    private static UIBoundableTransform? _calibrateVrButtonTransform;
    private static UITextComponent? _calibrateVrButtonLabel;
    private static UIButtonComponent? _bodyModeButton;
    private static UITextComponent? _bodyModeButtonLabel;
    private static UIButtonComponent? _bodyDecrementButton;
    private static UITextComponent? _bodyValueLabel;
    private static UIButtonComponent? _bodyIncrementButton;
    private static UIButtonComponent? _closeButton;
    private static UINavigationFlowMapComponent? _flowMapComponent;
    private static UICanvasInputComponent? _canvasInput;
    private static bool _subscribedGamepadBack;
    private static PawnComponent? _activePawn;
    private static LocalInputInterface? _savedCursorInput;
    private static bool _savedHideCursor;
    private static bool _hasSavedHideCursor;
    private static bool _isOpen;

    /// <summary>
    /// Gets a value indicating whether the injected direct-to-play pause menu is open.
    /// </summary>
    internal static bool IsOpen => _isOpen;

    /// <summary>
    /// Ensures the default pause menu is attached when the editor is running directly in play mode
    /// with a game mode or pawn that specifies no custom pause menu.
    /// </summary>
    internal static void RefreshForDirectPlayMode()
    {
        if (!TryResolveTarget(out PawnComponent? pawn, out CameraComponent? camera))
        {
            Detach();
            return;
        }

        EnsureAttached(pawn, camera!);
    }

    /// <summary>
    /// Toggles the injected pause menu in response to an Escape key or gamepad Start press during direct-to-play mode.
    /// </summary>
    internal static void ToggleFromPauseInput()
    {
        if (_isOpen)
        {
            SetOpen(false);
            return;
        }

        if (!TryResolveTarget(out PawnComponent? pawn, out CameraComponent? camera))
            return;

        EnsureAttached(pawn, camera!);
        SetOpen(true);
    }

    /// <summary>
    /// Toggles the injected pause menu in response to an Escape key press during direct-to-play mode.
    /// </summary>
    internal static void ToggleFromEscapeKey()
        => ToggleFromPauseInput();

    /// <summary>
    /// Shows or hides the injected pause menu with smooth fade, spring, and backdrop blur transitions.
    /// Restores gameplay input and cursor capture immediately when closing while the fade-out finishes.
    /// </summary>
    internal static void SetOpen(bool open)
    {
        if (open && (_overlayRoot is null || _overlayRoot.IsDestroyed || _overlayTransform is null))
            return;

        if (!open && !_isOpen)
            return;

        _isOpen = open;

        if (open)
        {
            RefreshVrControls();

            if (_overlayTransform is not null && !_overlayTransform.IsDestroyed)
            {
                bool wasCollapsed = _overlayTransform.Visibility == EVisibility.Collapsed;
                _overlayTransform.BlocksInputBehind = true;
                _overlayTransform.Visibility = EVisibility.Visible;
                _overlayTransform.InvalidateLayout();
                _overlayTransform.GetCanvasTransform()?.UpdateLayout();

                if (wasCollapsed)
                {
                    _scrimAnimator?.ApplyImmediate(in ScrimClosedState);
                    _cardAnimator?.ApplyImmediate(in CardClosedState);
                }
            }

            _scrimAnimator?.TransitionTo(
                in ScrimOpenState,
                OpenFadeTransition,
                UITransitionSpec.Instant,
                UITransitionSpec.Instant);

            _cardAnimator?.TransitionTo(
                in CardOpenState,
                OpenFadeTransition,
                OpenSpringTransition,
                OpenFadeTransition);

            ActivateGamepadNavigation();
            ApplyPausedInputAndCursorState();
            return;
        }

        // Restore gameplay input and cursor capture immediately while the fade-out and unblur finish.
        DeactivateGamepadNavigation();
        ClearButtonInteractionStates();
        RestoreInputAndCursorState();

        if (_overlayTransform is not null && !_overlayTransform.IsDestroyed)
            _overlayTransform.BlocksInputBehind = false;

        if (_scrimAnimator is not null && !_scrimAnimator.IsDestroyed)
        {
            _scrimAnimator.TransitionTo(
                in ScrimClosedState,
                CloseFadeTransition,
                UITransitionSpec.Instant,
                UITransitionSpec.Instant,
                onCompleted: CollapseIfClosed);
        }
        else
        {
            CollapseIfClosed();
        }

        if (_cardAnimator is not null && !_cardAnimator.IsDestroyed)
        {
            UIStyleProperties cardExit = CardClosedState;
            cardExit.VisualScale = new Vector2(0.96f, 0.96f);
            cardExit.VisualOffset = new Vector2(0.0f, -6.0f);
            _cardAnimator.TransitionTo(
                in cardExit,
                CloseFadeTransition,
                CloseFadeTransition,
                CloseFadeTransition);
        }
    }

    /// <summary>
    /// Updates the Resume button hover state through the standard interactable overlap path.
    /// </summary>
    internal static bool SetResumeButtonHovered(bool hovered)
    {
        if (!_isOpen || _resumeButton is null || _resumeButton.IsDestroyed)
            return false;

        _resumeButton.IsMouseDirectlyOver = hovered;
        return true;
    }

    /// <summary>
    /// Updates the Resume button pressed visual state through the standard button style path.
    /// </summary>
    internal static bool SetResumeButtonPressed(bool pressed)
    {
        if (!_isOpen || _resumeButton is null || _resumeButton.IsDestroyed)
            return false;

        _resumeButton.SetPressed(pressed);
        return true;
    }

    /// <summary>
    /// Activates the Resume button through the standard UI button click handler.
    /// </summary>
    internal static bool ClickResumeButton()
    {
        if (!_isOpen || _resumeButton is null || _resumeButton.IsDestroyed)
            return false;

        return _resumeButton.AccessibilityActivate();
    }

    private static void CollapseIfClosed()
    {
        if (_isOpen)
            return;

        if (_overlayTransform is not null && !_overlayTransform.IsDestroyed)
            _overlayTransform.Visibility = EVisibility.Collapsed;
    }

    private static void ClearButtonInteractionStates()
    {
        ClearButtonInteractionState(_resumeButton);
        ClearButtonInteractionState(_openXrButton);
        ClearButtonInteractionState(_calibrateVrButton);
        ClearButtonInteractionState(_bodyModeButton);
        ClearButtonInteractionState(_bodyDecrementButton);
        ClearButtonInteractionState(_bodyIncrementButton);
        ClearButtonInteractionState(_closeButton);
    }

    private static void ClearButtonInteractionState(UIButtonComponent? button)
    {
        if (button is null || button.IsDestroyed)
            return;

        button.SetPressed(false);
        button.IsGamepadSelected = false;
        button.IsMouseDirectlyOver = false;
    }

    private static void ActivateGamepadNavigation()
    {
        if (_canvasInput is null || _canvasInput.IsDestroyed)
            return;

        if (!_subscribedGamepadBack)
        {
            _canvasInput.GamepadBackPressed += OnGamepadBackPressed;
            _subscribedGamepadBack = true;
        }

        UINavigationFlowMap? flowMap = _flowMapComponent is { IsDestroyed: false } mapComp
            ? mapComp.FlowMap
            : null;
        _canvasInput.ActiveNavigationFlowMap = flowMap;
        _canvasInput.ResetGamepadNavigationState();

        UIInteractableComponent? initialTarget = flowMap?.ResolveInitialFocus() ?? _resumeButton;
        _canvasInput.FocusWithGamepad(initialTarget);
    }

    private static void DeactivateGamepadNavigation()
    {
        if (_canvasInput is not null && !_canvasInput.IsDestroyed)
        {
            if (_subscribedGamepadBack)
                _canvasInput.GamepadBackPressed -= OnGamepadBackPressed;

            if (_flowMapComponent is not null
                && ReferenceEquals(_canvasInput.ActiveNavigationFlowMap, _flowMapComponent.FlowMap))
            {
                _canvasInput.ActiveNavigationFlowMap = null;
            }

            _canvasInput.ResetGamepadNavigationState();
            _canvasInput.FocusedComponent = null;
        }

        _subscribedGamepadBack = false;
    }

    private static void OnGamepadBackPressed()
    {
        if (_isOpen)
            SetOpen(false);
    }

    /// <summary>
    /// Removes the injected pause menu overlay and restores any paused input or cursor state.
    /// </summary>
    internal static void Detach()
    {
        _isOpen = false;
        DeactivateGamepadNavigation();
        ClearButtonInteractionStates();
        RestoreInputAndCursorState();

        if (_overlayRoot is not null && !_overlayRoot.IsDestroyed)
            _overlayRoot.Destroy();

        if (_ownedCanvasCamera is not null && !_ownedCanvasCamera.IsDestroyed
            && ReferenceEquals(_ownedCanvasCamera.UserInterface, _attachedCanvas))
        {
            _ownedCanvasCamera.UserInterface = null;
        }

        if (_ownedCanvasNode is not null && !_ownedCanvasNode.IsDestroyed)
            _ownedCanvasNode.Destroy();

        _attachedCanvas = null;
        _ownedCanvasNode = null;
        _ownedCanvasCamera = null;
        _overlayRoot = null;
        _overlayTransform = null;
        _panelTransform = null;
        _scrimAnimator = null;
        _cardAnimator = null;
        _subtitleLabel = null;
        _resumeButton = null;
        _openXrButton = null;
        _openXrButtonLabel = null;
        _calibrateVrButton = null;
        _calibrateVrButtonTransform = null;
        _calibrateVrButtonLabel = null;
        _bodyModeButton = null;
        _bodyModeButtonLabel = null;
        _bodyDecrementButton = null;
        _bodyValueLabel = null;
        _bodyIncrementButton = null;
        _closeButton = null;
        _flowMapComponent = null;
        _canvasInput = null;
        _subscribedGamepadBack = false;
        _activePawn = null;
        _savedCursorInput = null;
        _savedHideCursor = false;
        _hasSavedHideCursor = false;
        _isOpen = false;
    }

    /// <summary>
    /// Determines whether the current session is a direct-to-play editor run with a game mode or
    /// pawn that has no custom pause menu configured.
    /// </summary>
    private static bool TryResolveTarget(out PawnComponent? pawn, out CameraComponent? camera)
    {
        pawn = null;
        camera = null;

        if (!Engine.PlayMode.ForcePlayWithoutTransitions || Engine.PlayMode.State != EPlayModeState.Play)
            return false;

        GameMode? activeGameMode = Engine.PlayMode.ActiveGameMode;
        if (activeGameMode?.HasCustomPauseMenu == true)
            return false;

        LocalPlayerController? localPlayer = ResolvePrimaryLocalPlayer();
        pawn = localPlayer?.ControlledPawn as PawnComponent;
        if (pawn is { IsDestroyed: true })
            pawn = null;

        if (activeGameMode is null && pawn is null)
            return false;

        if (pawn?.HasCustomPauseMenu == true)
            return false;

        camera = ResolveTargetCamera(pawn, localPlayer);
        return camera is not null && !camera.IsDestroyed;
    }

    private static LocalPlayerController? ResolvePrimaryLocalPlayer()
    {
        int defaultIndex = (int)Engine.PlayMode.Configuration.DefaultPlayerIndex;
        if (defaultIndex >= 0 && defaultIndex < Engine.State.LocalPlayers.Length
            && Engine.State.LocalPlayers[defaultIndex] is LocalPlayerController primary
            && primary.ControlledPawn is PawnComponent { IsDestroyed: false })
        {
            return primary;
        }

        for (int i = 0; i < Engine.State.LocalPlayers.Length; i++)
        {
            if (Engine.State.LocalPlayers[i] is LocalPlayerController candidate
                && candidate.ControlledPawn is PawnComponent { IsDestroyed: false })
            {
                return candidate;
            }
        }

        if (defaultIndex >= 0 && defaultIndex < Engine.State.LocalPlayers.Length
            && Engine.State.LocalPlayers[defaultIndex] is LocalPlayerController fallback)
        {
            return fallback;
        }

        return null;
    }

    private static CameraComponent? ResolveTargetCamera(PawnComponent? pawn, LocalPlayerController? localPlayer)
    {
        if (pawn?.GetCamera() is CameraComponent { IsDestroyed: false } pawnCamera)
            return pawnCamera;

        if (localPlayer?.Viewport is XRViewport { CameraComponent: CameraComponent { IsDestroyed: false } playerViewportCamera })
            return playerViewportCamera;

        for (int i = 0; i < RuntimeEngine.Windows.Count; i++)
        {
            XRWindow window = RuntimeEngine.Windows[i];
            for (int v = 0; v < window.Viewports.Count; v++)
            {
                if (window.Viewports[v].CameraComponent is CameraComponent { IsDestroyed: false } windowCamera)
                    return windowCamera;
            }
        }

        return null;
    }

    private static void EnsureAttached(PawnComponent? pawn, CameraComponent camera)
    {
        UICanvasComponent? screenCanvas = camera.UserInterface as UICanvasComponent;
        if (screenCanvas is { IsDestroyed: true } || screenCanvas?.CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen)
            screenCanvas = null;

        if (_overlayRoot is not null && !_overlayRoot.IsDestroyed
            && screenCanvas is not null && ReferenceEquals(_attachedCanvas, screenCanvas))
        {
            _activePawn = pawn;
            _canvasInput = EnsureCanvasInputLinked(screenCanvas.SceneNode, pawn);
            return;
        }

        Detach();

        SceneNode canvasNode;
        UICanvasComponent targetCanvas;
        if (screenCanvas is not null)
        {
            targetCanvas = screenCanvas;
            canvasNode = screenCanvas.SceneNode;
        }
        else
        {
            canvasNode = CreateOwnedScreenCanvas(camera, out targetCanvas);
            _ownedCanvasNode = canvasNode;
            _ownedCanvasCamera = camera;
        }

        _attachedCanvas = targetCanvas;
        _activePawn = pawn;
        _canvasInput = EnsureCanvasInputLinked(canvasNode, pawn);

        BuildOverlay(canvasNode);
    }

    private static SceneNode CreateOwnedScreenCanvas(CameraComponent camera, out UICanvasComponent canvas)
    {
        SceneNode canvasNode = new("PlayModePauseMenuCanvas");
        canvas = canvasNode.AddComponent<UICanvasComponent>()!;

        UICanvasTransform canvasTransform = canvas.CanvasTransform;
        canvasTransform.DrawSpace = ECanvasDrawSpace.Screen;
        canvasTransform.SetSize(new Vector2(1920.0f, 1080.0f));
        canvasTransform.Padding = Vector4.Zero;

        RuntimeWorld? runtimeWorld = camera.World as RuntimeWorld ?? Engine.WorldInstances.FirstOrDefault();
        if (runtimeWorld is not null)
            EditorWorldIntegrationRegistry.GetOrAttach(runtimeWorld).AddToEditorScene(canvasNode);

        if (canvasNode.IsActiveSelf)
            canvasNode.OnActivated();

        camera.UserInterface = canvas;
        return canvasNode;
    }

    private static UICanvasInputComponent? EnsureCanvasInputLinked(SceneNode canvasNode, PawnComponent? pawn)
    {
        UICanvasInputComponent? inputComponent = canvasNode.GetComponent<UICanvasInputComponent>();
        if (inputComponent is null)
            inputComponent = canvasNode.AddComponent<UICanvasInputComponent>();

        if (inputComponent is not null && pawn is not null && !ReferenceEquals(inputComponent.OwningPawn, pawn))
            inputComponent.OwningPawn = pawn;

        return inputComponent;
    }

    private static void BuildOverlay(SceneNode rootCanvasNode)
    {
        SceneNode backdropNode = new(rootCanvasNode, "PlayModePauseMenuRoot");
        UIBoundableTransform backdropTfm = backdropNode.SetTransform<UIBoundableTransform>();
        backdropTfm.MinAnchor = Vector2.Zero;
        backdropTfm.MaxAnchor = Vector2.One;
        backdropTfm.NormalizedPivot = Vector2.Zero;
        backdropTfm.Width = null;
        backdropTfm.Height = null;
        backdropTfm.Margins = Vector4.Zero;
        backdropTfm.BlocksInputBehind = false;
        backdropTfm.Visibility = EVisibility.Collapsed;

        UIMaterialComponent backdropMaterial = backdropNode.AddComponent<UIMaterialComponent>()!;
        backdropMaterial.Material = CreateSolidMaterial(ScrimClosedState.BackgroundColor!.Value);
        backdropMaterial.RenderPass = OverlayRenderPass;
        backdropMaterial.RenderCommand2D.ZIndex = BackdropZIndex;

        UIStyleAnimatorComponent scrimAnimator = backdropNode.AddComponent<UIStyleAnimatorComponent>()!;
        scrimAnimator.ApplyImmediate(in ScrimClosedState);

        SceneNode panelNode = new(backdropNode, "PlayModePauseMenuPanel");
        UIBoundableTransform panelTfm = panelNode.SetTransform<UIBoundableTransform>();
        panelTfm.MinAnchor = new Vector2(0.5f, 0.5f);
        panelTfm.MaxAnchor = new Vector2(0.5f, 0.5f);
        panelTfm.NormalizedPivot = new Vector2(0.5f, 0.5f);
        panelTfm.VisualPivot = new Vector2(0.5f, 0.5f);
        panelTfm.Width = PanelWidth;
        panelTfm.Height = PanelHeightBase;
        panelTfm.Margins = Vector4.Zero;
        panelTfm.BlocksInputBehind = true;

        UIMaterialComponent panelMaterial = panelNode.AddComponent<UIMaterialComponent>()!;
        panelMaterial.Material = CreateSolidMaterial(CardClosedState.BackgroundColor!.Value);
        panelMaterial.RenderPass = OverlayRenderPass;
        panelMaterial.RenderCommand2D.ZIndex = PanelZIndex;

        UIStyleAnimatorComponent cardAnimator = panelNode.AddComponent<UIStyleAnimatorComponent>()!;
        cardAnimator.ApplyImmediate(in CardClosedState);

        SceneNode stackNode = new(panelNode, "PlayModePauseMenuStack");
        UIListTransform stackTfm = stackNode.SetTransform<UIListTransform>();
        stackTfm.DisplayHorizontal = false;
        stackTfm.ItemSpacing = 12.0f;
        stackTfm.Padding = new Vector4(24.0f, 24.0f, 24.0f, 24.0f);
        stackTfm.Margins = Vector4.Zero;
        stackTfm.MinAnchor = Vector2.Zero;
        stackTfm.MaxAnchor = Vector2.One;
        stackTfm.NormalizedPivot = Vector2.Zero;
        stackTfm.Width = null;
        stackTfm.Height = null;
        stackTfm.ItemAlignment = EListAlignment.TopOrLeft;

        CreateLabel(stackNode, "PlayModePauseMenuTitle", "Paused", 28.0f, 34.0f, TitleColor);
        _subtitleLabel = CreateLabel(stackNode, "PlayModePauseMenuSubtitle", DefaultSubtitleText, 14.0f, 28.0f, SubtitleColor);

        _resumeButton = CreateButton(
            stackNode,
            "PlayModePauseMenuResumeButton",
            "Resume",
            ResumeButtonStyle,
            static _ => SetOpen(false),
            out _,
            out _);

        _openXrButton = CreateButton(
            stackNode,
            "PlayModePauseMenuOpenXrButton",
            "Enter VR (OpenXR)",
            VrButtonStyle,
            static _ => ToggleOpenXrFromPauseMenu(),
            out _,
            out _openXrButtonLabel);

        _calibrateVrButton = CreateButton(
            stackNode,
            "PlayModePauseMenuCalibrateVrButton",
            "Calibrate VR",
            VrButtonStyle,
            static _ => ToggleVrCalibrationFromPauseMenu(),
            out _calibrateVrButtonTransform,
            out _calibrateVrButtonLabel);
        _calibrateVrButtonTransform.Visibility = EVisibility.Collapsed;

        _bodyModeButton = CreateButton(
            stackNode,
            "PlayModePauseMenuBodyModeButton",
            "Body Scale: Height",
            VrButtonStyle,
            static _ => ToggleBodyMeasurementMode(),
            out UIBoundableTransform bodyModeTransform,
            out _bodyModeButtonLabel);
        bodyModeTransform.Height = CompactRowHeight;
        _bodyModeButtonLabel.FontSize = 15.0f;

        CreateBodyMeasurementStepperRow(
            stackNode,
            out _bodyDecrementButton,
            out _bodyValueLabel,
            out _bodyIncrementButton);

        _closeButton = CreateButton(
            stackNode,
            "PlayModePauseMenuCloseButton",
            "Close",
            CloseButtonStyle,
            static _ => CloseApplication(),
            out _,
            out _);

        UINavigationFlowMapComponent flowMapComponent = panelNode.AddComponent<UINavigationFlowMapComponent>()!;
        UINavigationFlowMap flowMap = flowMapComponent.FlowMap;
        flowMap.InitialFocus = _resumeButton;
        flowMap.AllowedDirections = EUINavigationAllowedDirections.Cardinal;
        flowMap.AllowSpatialFallback = false;
        flowMap.ConnectVerticalChain(wrap: true, _resumeButton, _openXrButton, _bodyModeButton, _bodyDecrementButton, _closeButton);
        flowMap.RepeatSettings = new UINavigationRepeatSettings
        {
            StickEngageThreshold = 0.55f,
            StickReleaseThreshold = 0.30f,
            AllowHoldRepeat = true,
            InitialRepeatDelaySeconds = 0.38f,
            RepeatIntervalSeconds = 0.16f,
            AllowDiagonalStick = false,
        };

        _overlayRoot = backdropNode;
        _overlayTransform = backdropTfm;
        _panelTransform = panelTfm;
        _scrimAnimator = scrimAnimator;
        _cardAnimator = cardAnimator;
        _flowMapComponent = flowMapComponent;
        RefreshVrControls();
    }

    /// <summary>
    /// Refreshes the OpenXR toggle label, optional calibration button, body measurement controls,
    /// panel height, and gamepad focus graph to match the current VR and user settings state.
    /// </summary>
    private static void RefreshVrControls()
    {
        bool vrRequested = EditorOpenXrPawnSwitcher.IsRequested;
        if (_openXrButtonLabel is not null && !_openXrButtonLabel.IsDestroyed)
        {
            _openXrButtonLabel.Text = !EditorOpenXrPawnSwitcher.CanToggle
                ? EditorOpenXrPawnSwitcher.Status
                : vrRequested
                    ? "Exit VR (OpenXR)"
                    : "Enter VR (OpenXR)";
        }

        VRPlayerCharacterComponent? activeVrPlayer = EditorOpenXrPawnSwitcher.ActiveVrPlayer;
        bool showCalibrate = vrRequested && activeVrPlayer is not null && !activeVrPlayer.IsDestroyed;
        if (_calibrateVrButtonTransform is not null && !_calibrateVrButtonTransform.IsDestroyed)
            _calibrateVrButtonTransform.Visibility = showCalibrate ? EVisibility.Visible : EVisibility.Collapsed;

        if (showCalibrate && _calibrateVrButtonLabel is not null && !_calibrateVrButtonLabel.IsDestroyed)
            _calibrateVrButtonLabel.Text = activeVrPlayer!.IsCalibrating ? "Cancel Calibration" : "Calibrate VR";

        RefreshBodyMeasurementControls();

        if (_panelTransform is not null && !_panelTransform.IsDestroyed)
        {
            _panelTransform.Height = showCalibrate ? PanelHeightWithCalibrate : PanelHeightBase;
            _panelTransform.InvalidateLayout();
        }

        if (_isOpen && _overlayTransform is not null && !_overlayTransform.IsDestroyed)
            _overlayTransform.GetCanvasTransform()?.UpdateLayout();

        if (_subtitleLabel is not null && !_subtitleLabel.IsDestroyed)
        {
            string? error = EditorOpenXrPawnSwitcher.LastError;
            _subtitleLabel.Text = string.IsNullOrWhiteSpace(error) ? DefaultSubtitleText : error;
        }

        if (_flowMapComponent is { IsDestroyed: false } mapComp
            && _resumeButton is not null
            && _openXrButton is not null
            && _bodyModeButton is not null
            && _bodyDecrementButton is not null
            && _bodyIncrementButton is not null
            && _closeButton is not null)
        {
            UINavigationFlowMap flowMap = mapComp.FlowMap;
            flowMap.Clear();
            flowMap.AllowedDirections = EUINavigationAllowedDirections.Cardinal;
            flowMap.InitialFocus = _resumeButton;
            if (showCalibrate && _calibrateVrButton is not null)
                flowMap.ConnectVerticalChain(wrap: true, _resumeButton, _openXrButton, _calibrateVrButton, _bodyModeButton, _bodyDecrementButton, _closeButton);
            else
                flowMap.ConnectVerticalChain(wrap: true, _resumeButton, _openXrButton, _bodyModeButton, _bodyDecrementButton, _closeButton);
            flowMap.ConnectHorizontalChain(wrap: true, _bodyDecrementButton, _bodyIncrementButton);
            flowMap.SetEdge(_bodyIncrementButton, EUINavigationDirection.Up, _bodyModeButton, EUINavigationRule.Explicit, bidirectional: false);
            flowMap.SetEdge(_bodyIncrementButton, EUINavigationDirection.Down, _closeButton, EUINavigationRule.Explicit, bidirectional: false);
        }
    }

    /// <summary>
    /// Updates the body measurement mode button and value readout from the active user settings.
    /// </summary>
    private static void RefreshBodyMeasurementControls()
    {
        UserSettings? settings = Engine.UserSettings;
        EVrBodyMeasurementMode mode = settings?.BodyMeasurementMode ?? EVrBodyMeasurementMode.Height;
        bool isArmSpan = mode == EVrBodyMeasurementMode.ArmSpan;

        if (_bodyModeButtonLabel is not null && !_bodyModeButtonLabel.IsDestroyed)
            _bodyModeButtonLabel.Text = isArmSpan ? "Body Scale: Arm Span" : "Body Scale: Height";

        if (_bodyValueLabel is not null && !_bodyValueLabel.IsDestroyed)
        {
            float? valueMeters = isArmSpan ? settings?.PlayerArmSpan : settings?.PlayerHeight;
            string prefix = isArmSpan ? "Arm Span" : "Height";
            _bodyValueLabel.Text = FormatBodyMeasurement(prefix, valueMeters);
        }
    }

    private static string FormatBodyMeasurement(string prefix, float? meters)
    {
        if (meters is not float value || !float.IsFinite(value))
            return $"{prefix}: Unset";

        int totalInches = (int)MathF.Round(value / 0.0254f);
        int feet = totalInches / 12;
        int inches = totalInches % 12;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}: {value:0.00} m ({feet}'{inches}\")");
    }

    /// <summary>
    /// Switches between standing height and arm span in user settings and persists the change.
    /// </summary>
    private static void ToggleBodyMeasurementMode()
    {
        if (Engine.UserSettings is not { } settings)
            return;

        settings.BodyMeasurementMode = settings.BodyMeasurementMode == EVrBodyMeasurementMode.ArmSpan
            ? EVrBodyMeasurementMode.Height
            : EVrBodyMeasurementMode.ArmSpan;
        RefreshBodyMeasurementControls();
        _ = SaveUserSettingsAsync();
    }

    /// <summary>
    /// Adjusts the active standing height or arm span measurement by one centimeter and persists it.
    /// </summary>
    private static void StepActiveBodyMeasurement(float deltaMeters)
    {
        if (Engine.UserSettings is not { } settings)
            return;

        bool isArmSpan = settings.BodyMeasurementMode == EVrBodyMeasurementMode.ArmSpan;
        float maxMeters = isArmSpan ? MaxPlayerArmSpanMeters : MaxPlayerHeightMeters;
        float? current = isArmSpan ? settings.PlayerArmSpan : settings.PlayerHeight;
        float next = current is float existing && float.IsFinite(existing)
            ? Math.Clamp(MathF.Round((existing + deltaMeters) * 100.0f) / 100.0f, MinBodyMeasurementMeters, maxMeters)
            : DefaultBodyMeasurementMeters;

        if (isArmSpan)
            settings.PlayerArmSpan = next;
        else
            settings.PlayerHeight = next;

        RefreshBodyMeasurementControls();
        _ = SaveUserSettingsAsync();
    }

    private static async Task SaveUserSettingsAsync()
    {
        try
        {
            await Engine.SaveProjectUserSettingsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to save user settings from the pause menu: {ex.Message}");
        }
    }

    private static void CreateBodyMeasurementStepperRow(
        SceneNode parent,
        out UIButtonComponent decrementButton,
        out UITextComponent valueLabel,
        out UIButtonComponent incrementButton)
    {
        SceneNode rowNode = new(parent, "PlayModePauseMenuBodyValueRow");
        UIListTransform rowTransform = rowNode.SetTransform<UIListTransform>();
        rowTransform.DisplayHorizontal = true;
        rowTransform.ItemSpacing = StepperSpacing;
        rowTransform.Padding = Vector4.Zero;
        rowTransform.Margins = Vector4.Zero;
        rowTransform.MinAnchor = new Vector2(0.0f, 0.0f);
        rowTransform.MaxAnchor = new Vector2(1.0f, 0.0f);
        rowTransform.NormalizedPivot = Vector2.Zero;
        rowTransform.Width = null;
        rowTransform.Height = CompactRowHeight;
        rowTransform.ItemAlignment = EListAlignment.TopOrLeft;

        decrementButton = CreateButton(
            rowNode,
            "PlayModePauseMenuBodyDecrementButton",
            "-",
            VrButtonStyle,
            static _ => StepActiveBodyMeasurement(-BodyMeasurementStepMeters),
            out UIBoundableTransform decTransform,
            out UITextComponent decLabel);
        decTransform.MinAnchor = Vector2.Zero;
        decTransform.MaxAnchor = Vector2.Zero;
        decTransform.Width = StepperButtonWidth;
        decTransform.Height = CompactRowHeight;
        decLabel.FontSize = 20.0f;

        SceneNode valueNode = new(rowNode, "PlayModePauseMenuBodyValueDisplay");
        UIBoundableTransform valueTransform = valueNode.SetTransform<UIBoundableTransform>();
        valueTransform.MinAnchor = Vector2.Zero;
        valueTransform.MaxAnchor = Vector2.Zero;
        valueTransform.NormalizedPivot = Vector2.Zero;
        valueTransform.Width = PanelWidth - 48.0f - (StepperButtonWidth * 2.0f) - (StepperSpacing * 2.0f);
        valueTransform.Height = CompactRowHeight;
        valueTransform.Margins = Vector4.Zero;

        valueLabel = valueNode.AddComponent<UITextComponent>()!;
        valueLabel.Text = "Height: Unset";
        valueLabel.FontSize = 14.5f;
        valueLabel.Color = TitleColor;
        valueLabel.HorizontalAlignment = EHorizontalAlignment.Center;
        valueLabel.VerticalAlignment = EVerticalAlignment.Center;
        valueLabel.WrapMode = FontGlyphSet.EWrapMode.None;
        valueLabel.HideOverflow = false;
        valueLabel.RenderPass = OverlayRenderPass;
        valueLabel.RenderCommand2D.ZIndex = TextZIndex;

        incrementButton = CreateButton(
            rowNode,
            "PlayModePauseMenuBodyIncrementButton",
            "+",
            VrButtonStyle,
            static _ => StepActiveBodyMeasurement(BodyMeasurementStepMeters),
            out UIBoundableTransform incTransform,
            out UITextComponent incLabel);
        incTransform.MinAnchor = Vector2.Zero;
        incTransform.MaxAnchor = Vector2.Zero;
        incTransform.Width = StepperButtonWidth;
        incTransform.Height = CompactRowHeight;
        incLabel.FontSize = 20.0f;
    }

    /// <summary>
    /// Starts or stops the OpenXR VR session from the direct-to-play pause menu.
    /// Uses FirstPerson desktop view on entry so play-mode controller and trigger input
    /// routes to the VR pawn.
    /// </summary>
    private static void ToggleOpenXrFromPauseMenu()
    {
        if (!EditorOpenXrPawnSwitcher.CanToggle)
        {
            RefreshVrControls();
            return;
        }

        bool toggled;
        if (EditorOpenXrPawnSwitcher.IsRequested)
        {
            toggled = EditorOpenXrPawnSwitcher.TrySetEnabled(false);
        }
        else
        {
            EditorOpenXrPawnSwitcher.SetDesktopView(EVrDesktopView.FirstPerson);
            EditorOpenXrRuntimeChoice choice = RuntimeBootstrapState.Settings.VR.Mode == UnitTestingVrLaunchMode.MonadoOpenXR
                ? EditorOpenXrRuntimeChoice.Monado
                : EditorOpenXrRuntimeChoice.SteamVR;
            toggled = EditorOpenXrPawnSwitcher.TrySetEnabled(true, choice);
        }

        if (toggled)
        {
            SetOpen(false);
            return;
        }

        RefreshVrControls();
    }

    private static void ToggleVrCalibrationFromPauseMenu()
    {
        if (EditorOpenXrPawnSwitcher.ActiveVrPlayer is not { IsDestroyed: false } player)
        {
            RefreshVrControls();
            return;
        }

        if (player.IsCalibrating)
            player.CancelCalibration();
        else
            player.BeginCalibration();

        SetOpen(false);
    }

    private static void ApplyPausedInputAndCursorState()
    {
        PawnComponent? pawn = _activePawn is { IsDestroyed: false } current ? current : null;
        LocalInputInterface? localInput = pawn?.LocalInput as LocalInputInterface
            ?? ResolvePrimaryLocalPlayer()?.Input as LocalInputInterface;

        if (!_hasSavedHideCursor && localInput is not null)
        {
            _savedCursorInput = localInput;
            _savedHideCursor = localInput.HideCursor;
            _hasSavedHideCursor = true;
        }

        if (pawn is not null)
            pawn.IsGameplayInputPaused = true;

        if (localInput is not null && localInput.HideCursor)
            localInput.HideCursor = false;
    }

    private static void RestoreInputAndCursorState()
    {
        PawnComponent? pawn = _activePawn is { IsDestroyed: false } current ? current : null;
        if (pawn is not null)
            pawn.IsGameplayInputPaused = false;

        if (_hasSavedHideCursor)
        {
            if (pawn is not CharacterPawnComponent && _savedCursorInput is not null)
                _savedCursorInput.HideCursor = _savedHideCursor;

            _savedCursorInput = null;
            _savedHideCursor = false;
            _hasSavedHideCursor = false;
        }
    }

    /// <summary>
    /// Closes the application window cleanly when the user selects Close from the pause menu.
    /// </summary>
    private static void CloseApplication()
    {
        if (_overlayTransform is not null && !_overlayTransform.IsDestroyed)
            _overlayTransform.Visibility = EVisibility.Collapsed;

        _isOpen = false;
        DeactivateGamepadNavigation();

        EditorImGuiUI.ForceAllowWindowCloseForShutdown();
        Engine.InvokeOnMainThread(
            Engine.ShutDown,
            "EditorPlayModePauseMenu.Close",
            executeNowIfAlreadyMainThread: true);
    }

    private static XRMaterial CreateSolidMaterial(ColorF4 color)
    {
        XRMaterial material = XRMaterial.CreateUnlitColorMaterialForward(color);
        material.EnableTransparency();
        return material;
    }

    private static UITextComponent CreateLabel(
        SceneNode parent,
        string name,
        string content,
        float fontSize,
        float height,
        ColorF4 color)
    {
        SceneNode node = new(parent, name);
        UIBoundableTransform transform = node.SetTransform<UIBoundableTransform>();
        transform.MinAnchor = new Vector2(0.0f, 0.0f);
        transform.MaxAnchor = new Vector2(1.0f, 0.0f);
        transform.NormalizedPivot = Vector2.Zero;
        transform.Width = null;
        transform.Height = height;
        transform.Margins = Vector4.Zero;

        UITextComponent text = node.AddComponent<UITextComponent>()!;
        text.Text = content;
        text.FontSize = fontSize;
        text.Color = color;
        text.HorizontalAlignment = EHorizontalAlignment.Center;
        text.VerticalAlignment = EVerticalAlignment.Center;
        text.WrapMode = FontGlyphSet.EWrapMode.None;
        text.HideOverflow = false;
        text.RenderPass = OverlayRenderPass;
        text.RenderCommand2D.ZIndex = TextZIndex;
        return text;
    }

    private static UIButtonComponent CreateButton(
        SceneNode parent,
        string name,
        string labelText,
        UIStyle style,
        Action<UIInteractableComponent> onClick,
        out UIBoundableTransform buttonTransform,
        out UITextComponent label)
    {
        SceneNode buttonNode = new(parent, name);
        buttonTransform = buttonNode.SetTransform<UIBoundableTransform>();
        buttonTransform.MinAnchor = new Vector2(0.0f, 0.0f);
        buttonTransform.MaxAnchor = new Vector2(1.0f, 0.0f);
        buttonTransform.NormalizedPivot = Vector2.Zero;
        buttonTransform.VisualPivot = new Vector2(0.5f, 0.5f);
        buttonTransform.Width = null;
        buttonTransform.Height = ButtonHeight;
        buttonTransform.Margins = Vector4.Zero;

        UIMaterialComponent background = buttonNode.AddComponent<UIMaterialComponent>()!;
        background.Material = CreateSolidMaterial(style.Base.BackgroundColor ?? ColorF4.White);
        background.RenderPass = OverlayRenderPass;
        background.RenderCommand2D.ZIndex = ButtonBackgroundZIndex;

        SceneNode labelNode = new(buttonNode, $"{name}Label");
        UIBoundableTransform labelTransform = labelNode.SetTransform<UIBoundableTransform>();
        labelTransform.MinAnchor = Vector2.Zero;
        labelTransform.MaxAnchor = Vector2.One;
        labelTransform.NormalizedPivot = Vector2.Zero;
        labelTransform.Width = null;
        labelTransform.Height = null;
        labelTransform.Margins = Vector4.Zero;

        label = labelNode.AddComponent<UITextComponent>()!;
        label.Text = labelText;
        label.FontSize = 17.0f;
        label.Color = style.Base.TextColor ?? ColorF4.White;
        label.HorizontalAlignment = EHorizontalAlignment.Center;
        label.VerticalAlignment = EVerticalAlignment.Center;
        label.WrapMode = FontGlyphSet.EWrapMode.None;
        label.HideOverflow = false;
        label.RenderPass = OverlayRenderPass;
        label.RenderCommand2D.ZIndex = TextZIndex;

        UIButtonComponent button = buttonNode.AddComponent<UIButtonComponent>()!;
        button.Style = style;
        button.RegisterClickActions(onClick);

        return button;
    }
}

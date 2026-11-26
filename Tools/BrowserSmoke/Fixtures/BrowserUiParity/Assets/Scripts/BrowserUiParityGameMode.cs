using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using XREngine;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace BrowserUiParity;

/// <summary>Wires normal shared controls in the authored world and exposes their results as UI text.</summary>
public sealed class BrowserUiParityGameMode : GameMode
{
    private string _fixtureMarker = string.Empty;
    private bool _clippingEnabled = true;
    private readonly List<(UIButtonComponent Button, Action<UIInteractableComponent> Action)> _actions = [];
    private SceneNode? _root;
    private SceneNode? _target;
    private SceneNode? _targetHome;
    private SceneNode? _targetAlternate;
    private UIButtonComponent? _targetButton;
    private UIMaterialComponent? _clip;
    private UITextInputComponent? _status;
    private UITextInputComponent? _singleLine;
    private UIToggleComponent? _toggle;
    private int _actionCount;
    private int _submitCount;
    private int _cancelCount;
    private bool _moved;
    private bool _rotated;
    private bool _renamed;

    /// <summary>Identifies this authored scene after source loading and cooked hydration.</summary>
    public string FixtureMarker { get => _fixtureMarker; set => SetField(ref _fixtureMarker, value); }

    /// <summary>The actual property edited by the shared checkbox control.</summary>
    public bool ClippingEnabled
    {
        get => _clippingEnabled;
        set
        {
            if (!SetField(ref _clippingEnabled, value) || _clip is null)
                return;
            _clip.ClipToBounds = value;
            SetPanelColor("Clip Toggle", value ? new(0.1f, 0.65f, 0.3f, 1) : new(0.35f, 0.35f, 0.35f, 1));
            RecordAction();
        }
    }

    public override XRComponent? CreateDefaultPawn(ELocalPlayerIndex playerIndex) => null;

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(BrowserUiParityGameMode))]
    public override void OnBeginPlay()
    {
        base.OnBeginPlay();
        if (WorldInstance is not RuntimeWorld world)
            throw new InvalidOperationException("The UI fixture requires the engine runtime world.");
        SetField(ref _root, null);
        for (int index = 0; index < world.RootNodes.Count; index++)
            if (world.RootNodes[index].Name == "Browser UI Fixture")
            {
                SetField(ref _root, world.RootNodes[index]);
                break;
            }
        if (_root is null)
            throw new InvalidOperationException("The authored UI root is missing from the runtime world.");
        BrowserUiParityPawnComponent pawn = Require<BrowserUiParityPawnComponent>("Inspection Camera");
        CameraComponent camera = Require<CameraComponent>("Inspection Camera");
        if (!ReferenceEquals(pawn.CameraComponent, camera) || camera.RenderPipelineSource is not DefaultRenderPipeline)
            throw new InvalidOperationException("The authored pawn/camera alias and Default pipeline must survive hydration.");
        UICanvasComponent screen = Require<UICanvasComponent>("Screen Canvas");
        UICanvasComponent offscreen = Require<UICanvasComponent>("Offscreen Canvas");
        UICanvasInputComponent screenInput = Require<UICanvasInputComponent>("Screen Canvas");
        UICanvasInputComponent offscreenInput = Require<UICanvasInputComponent>("Offscreen Canvas");
        if (!ReferenceEquals(screenInput.OwningPawn, pawn) || !ReferenceEquals(offscreenInput.OwningPawn, pawn))
            throw new InvalidOperationException("Both saved canvas input owners must resolve to the authored pawn.");
        if (screen.CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen ||
            offscreen.CanvasTransform.DrawSpace != ECanvasDrawSpace.World ||
            !offscreen.UseOffscreenRenderingForNonScreenSpaces())
            throw new InvalidOperationException("The saved screen and owned world canvas routes must remain distinct.");

        // Re-establish event subscriptions after ordinary cooked hydration, which suppresses notifications.
        BindInput(screenInput, screen, pawn);
        BindInput(offscreenInput, offscreen, pawn);
        pawn.UserInterfaceInput = screenInput;
        camera.UserInterface = screen;
        pawn.PossessByLocalPlayer(ELocalPlayerIndex.One);

        SetField(ref _clip, Require<UIMaterialComponent>("Clip Parent"));
        SetField(ref _status, Require<UITextInputComponent>("Action Status"));
        SetField(ref _singleLine, Require<UITextInputComponent>("Single Line"));
        SetField(ref _toggle, Require<UIToggleComponent>("Clip Toggle"));
        SetField(ref _target, Node("Mutation Target"));
        SetField(ref _targetHome, Node("Target Home"));
        SetField(ref _targetAlternate, Node("Target Alternate"));
        SetField(ref _targetButton, Require<UIButtonComponent>("Mutation Target"));
        _toggle!.Property = typeof(BrowserUiParityGameMode).GetProperty(nameof(ClippingEnabled))
            ?? throw new InvalidOperationException("The shared checkbox property is missing.");
        _toggle!.Targets = [this];
        _toggle!.UpdateInterval = TimeSpan.Zero;

        // Keep painter order across solid panels, images, button backgrounds and glyphs.
        _root!.IterateComponents<UIMaterialComponent>(static quad =>
            quad.RenderPass = (int)EDefaultRenderPass.TransparentForward, iterateChildHierarchy: true);

        SetPanelColor("Screen Backdrop", new(0.04f, 0.07f, 0.12f, 1));
        SetPanelColor("Clip Parent", new(0, 0.25f, 0.55f, 1));
        SetPanelColor("Clip Toggle", new(0.1f, 0.65f, 0.3f, 1));
        SetPanelColor("Target Home", new(0.08f, 0.12f, 0.2f, 1));
        SetPanelColor("Target Alternate", new(0.08f, 0.12f, 0.2f, 1));
        SetPanelColor("World Bottom Left", new(1, 0, 0, 1));
        SetPanelColor("World Bottom Right", new(0, 1, 0, 1));
        SetPanelColor("World Top Left", new(0, 0, 1, 1));
        SetPanelColor("World Top Right", new(1, 1, 0, 1));
        SetPanelColor("World Half Red", new(1, 0, 0, 0.5f));
        SetPanelColor("World Half Blue", new(0, 0, 1, 0.5f));

        Bind("Count Action", CountAction);
        Bind("Clipped Action", CountAction);
        Bind("Offscreen Action", CountAction);
        Bind("Mutation Target", CountAction);
        Bind("Rename Target", RenameTarget);
        Bind("Hide Target", HideTarget);
        Bind("Deactivate Target", DeactivateTarget);
        Bind("Reparent Target", ReparentTarget);
        Bind("Remove Target", RemoveTarget);
        Bind("Restore Target", RestoreTarget);
        Bind("Move Clip", MoveClip);
        Bind("Rotate Clip", RotateClip);
        _singleLine!.Submitted += Submitted;
        _singleLine!.Cancelled += Cancelled;
        SetField(ref _actionCount, 0);
        SetField(ref _submitCount, 0);
        SetField(ref _cancelCount, 0);
        PublishStatus();
    }

    public override void OnEndPlay()
    {
        foreach ((UIButtonComponent button, Action<UIInteractableComponent> action) in _actions)
            button.UnregisterClickActions(action);
        _actions.Clear();
        if (_singleLine is not null)
        {
            _singleLine.Submitted -= Submitted;
            _singleLine.Cancelled -= Cancelled;
        }
        if (_toggle is not null)
        {
            _toggle.Targets = null;
            _toggle.Property = null;
        }
        RestoreTargetState();
        if (_clip is not null)
        {
            _clip.ClipToBounds = true;
            _clip.BoundableTransform.Translation = new(32, 124);
            _clip.BoundableTransform.RotationDegrees = 0;
        }
        SetField(ref _clippingEnabled, true);
        SetField(ref _moved, false);
        SetField(ref _rotated, false);
        base.OnEndPlay();
    }

    private static void BindInput(UICanvasInputComponent input, UICanvasComponent canvas, BrowserUiParityPawnComponent pawn)
    {
        input.OwningPawn = null;
        input.Canvas = canvas;
        input.OwningPawn = pawn;
    }

    private SceneNode Node(string name) => _root?.FindDescendantByName(name)
        ?? throw new InvalidOperationException($"The saved UI node '{name}' is missing.");

    private T Require<T>(string name) where T : XRComponent => Node(name).GetComponent<T>()
        ?? throw new InvalidOperationException($"The saved UI node '{name}' lacks {typeof(T).Name}.");

    private void SetPanelColor(string name, ColorF4 color)
    {
        UIMaterialComponent quad = Require<UIMaterialComponent>(name);
        (quad.Material ?? throw new InvalidOperationException($"The saved UI quad '{name}' lacks a material."))
            .SetVector4("MatColor", color);
        quad.RenderPass = (int)EDefaultRenderPass.TransparentForward;
    }

    private void Bind(string name, Action<UIInteractableComponent> action)
    {
        UIButtonComponent button = Require<UIButtonComponent>(name);
        button.RegisterClickActions(action);
        _actions.Add((button, action));
    }

    private void CountAction(UIInteractableComponent _) => RecordAction();
    private void Submitted(UITextInputComponent _)
    {
        SetField(ref _submitCount, _submitCount + 1);
        RecordAction();
    }
    private void Cancelled(UITextInputComponent _)
    {
        SetField(ref _cancelCount, _cancelCount + 1);
        RecordAction();
    }
    private void RecordAction()
    {
        SetField(ref _actionCount, _actionCount + 1);
        PublishStatus();
    }
    private void PublishStatus()
    {
        if (_status is null)
            return;
        string value = $"Actions: {_actionCount}; Submits: {_submitCount}; Cancels: {_cancelCount}";
        _status.Text = value;
        _status.AccessibilityLabel = value;
    }
    private void RenameTarget(UIInteractableComponent _)
    {
        SetField(ref _renamed, !_renamed);
        string label = _renamed ? "Renamed Target" : "Mutation Target";
        _targetButton!.AccessibilityLabel = label;
        _targetButton!.TextComponent!.Text = label;
        RecordAction();
    }
    private void HideTarget(UIInteractableComponent _)
    {
        UITransform transform = _targetButton!.UITransform;
        if (transform.IsVisible) transform.Hide();
        else transform.Show();
        RecordAction();
    }
    private void DeactivateTarget(UIInteractableComponent _)
    {
        _targetButton!.IsActive = !_targetButton!.IsActive;
        RecordAction();
    }
    private void ReparentTarget(UIInteractableComponent _)
    {
        _target!.Parent = ReferenceEquals(_target!.Parent, _targetHome) ? _targetAlternate : _targetHome;
        RecordAction();
    }
    private void RemoveTarget(UIInteractableComponent _)
    {
        _target!.IsActiveSelf = false;
        _target!.Parent = null;
        RecordAction();
    }
    private void RestoreTarget(UIInteractableComponent _)
    {
        RestoreTargetState();
        RecordAction();
    }
    private void RestoreTargetState()
    {
        if (_target is null || _targetButton is null || _targetHome is null)
            return;
        _target.Parent = _targetHome;
        _target.IsActiveSelf = true;
        _targetButton.IsActive = true;
        _targetButton.UITransform.Show();
        _targetButton.AccessibilityLabel = "Mutation Target";
        _targetButton.TextComponent!.Text = "Mutation Target";
        SetField(ref _renamed, false);
    }
    private void MoveClip(UIInteractableComponent _)
    {
        SetField(ref _moved, !_moved);
        _clip!.BoundableTransform.Translation = new(_moved ? 56 : 32, 124);
        RecordAction();
    }
    private void RotateClip(UIInteractableComponent _)
    {
        SetField(ref _rotated, !_rotated);
        _clip!.BoundableTransform.RotationDegrees = _rotated ? 15 : 0;
        RecordAction();
    }
}

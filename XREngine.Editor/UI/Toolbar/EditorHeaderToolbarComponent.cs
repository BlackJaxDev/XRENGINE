using System.Numerics;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Components.Editing;

namespace XREngine.Editor.UI.Toolbar;

/// <summary>
/// The header row under the native main menu, like the ImGui editor toolbar: the transform mode buttons, the
/// transform space buttons, and the play controls in the middle of the row with the play state text.
/// </summary>
/// <remarks>
/// A late tick reads the transform tool and play mode state each frame and marks the buttons of the current mode
/// and space as selected. The ImGui toolbar also has snap values and the OpenXR and graphics API selectors. Its
/// snap values do not reach the transform tool, and the selectors need popups that the native header does not
/// have yet, so this row leaves them out.
/// </remarks>
[RequiresTransform(typeof(UIBoundableTransform))]
public sealed class EditorHeaderToolbarComponent : UIComponent
{
    private const float ButtonHeight = 26.0f;
    private const float SectionGap = 16.0f;

    private UIButtonComponent? _translate;
    private UIButtonComponent? _rotate;
    private UIButtonComponent? _scale;
    private UIButtonComponent? _world;
    private UIButtonComponent? _local;
    private UIButtonComponent? _parent;
    private UIButtonComponent? _screen;
    private UIButtonComponent? _playPause;
    private UITextComponent? _playPauseLabel;
    private UIButtonComponent? _stop;
    private UIButtonComponent? _step;
    private UITextComponent? _stateLabel;
    private bool _built;
    private bool _tickRegistered;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (!_built)
            Build();
        if (!_tickRegistered)
        {
            _tickRegistered = true;
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateState);
        }
        UpdateState();
    }

    protected override void OnComponentDeactivated()
    {
        if (_tickRegistered)
        {
            _tickRegistered = false;
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateState);
        }
        base.OnComponentDeactivated();
    }

    private void Build()
    {
        _built = true;
        SceneNode root = SceneNode;
        UIMaterialComponent background = root.AddComponent<UIMaterialComponent>()!;
        EditorGlassTheme.ApplyStatic(background, EditorGlassTheme.MenuBar);

        // Left group: transform mode and space, like "Transform: W E R" and "Space: ..." in ImGui.
        SceneNode left = root.NewChildWithTransform(out UIListTransform leftList, "HeaderTools");
        leftList.DisplayHorizontal = true;
        leftList.ItemAlignment = EListAlignment.TopOrLeft;
        leftList.ItemSpacing = 2.0f;
        leftList.Padding = new Vector4(8.0f, 0.0f, 8.0f, 0.0f);
        leftList.MinAnchor = Vector2.Zero;
        leftList.MaxAnchor = new Vector2(0.0f, 1.0f);
        leftList.Width = 560.0f;

        CreateSectionLabel(left, "Transform:", 70.0f);
        _translate = CreateToolButton(left, "W", "Translate (W)", 30.0f, () => TransformTool3D.TransformMode = ETransformMode.Translate);
        _rotate = CreateToolButton(left, "E", "Rotate (E)", 30.0f, () => TransformTool3D.TransformMode = ETransformMode.Rotate);
        _scale = CreateToolButton(left, "R", "Scale (R)", 30.0f, () => TransformTool3D.TransformMode = ETransformMode.Scale);
        CreateGap(left, SectionGap);
        CreateSectionLabel(left, "Space:", 48.0f);
        _world = CreateToolButton(left, "World", "World Space", 56.0f, () => TransformTool3D.TransformSpace = ETransformSpace.World);
        _local = CreateToolButton(left, "Local", "Local Space", 52.0f, () => TransformTool3D.TransformSpace = ETransformSpace.Local);
        _parent = CreateToolButton(left, "Parent", "Parent Space", 60.0f, () => TransformTool3D.TransformSpace = ETransformSpace.Parent);
        _screen = CreateToolButton(left, "Screen", "Screen Space", 62.0f, () => TransformTool3D.TransformSpace = ETransformSpace.Screen);

        // Middle group: the play controls at the center of the row, like the ImGui toolbar.
        SceneNode middle = root.NewChildWithTransform(out UIListTransform middleList, "HeaderPlay");
        middleList.DisplayHorizontal = true;
        middleList.ItemAlignment = EListAlignment.TopOrLeft;
        middleList.ItemSpacing = 2.0f;
        middleList.MinAnchor = new Vector2(0.5f, 0.0f);
        middleList.MaxAnchor = new Vector2(0.5f, 1.0f);
        middleList.NormalizedPivot = new Vector2(0.5f, 0.0f);
        middleList.Width = 260.0f;
        var playPause = NativeUIElements.CreateButton(middle, "Play", _ => PlayOrPause(), width: 64.0f, height: ButtonHeight,
            margins: new Vector4(1.0f, 0.0f, 1.0f, 0.0f));
        _playPause = playPause.Button;
        _playPauseLabel = playPause.Label;
        _playPause.AccessibilityLabel = "Play (F5)";
        _stop = CreateToolButton(middle, "Stop", "Stop (Shift+F5)", 56.0f, EditorState.RequestExitPlayMode);
        _step = CreateToolButton(middle, "Step", "Step Frame (F6)", 56.0f, EditorState.StepFrame);
        SceneNode stateNode = middle.NewChild<UITextComponent>(out var state);
        stateNode.GetTransformAs<UIBoundableTransform>(true)!.Width = 80.0f;
        state.FontSize = EditorGlassTheme.CaptionFontSize;
        state.VerticalAlignment = EVerticalAlignment.Center;
        state.BoundableTransform.Margins = new Vector4(8.0f, 0.0f, 0.0f, 0.0f);
        _stateLabel = state;
    }

    private static void CreateSectionLabel(SceneNode parent, string text, float width)
    {
        SceneNode node = parent.NewChild<UITextComponent>(out var label);
        node.GetTransformAs<UIBoundableTransform>(true)!.Width = width;
        label.Text = text;
        label.FontSize = EditorGlassTheme.CaptionFontSize;
        label.Color = EditorGlassTheme.TextSecondary;
        label.VerticalAlignment = EVerticalAlignment.Center;
    }

    private static void CreateGap(SceneNode parent, float width)
        => parent.NewChild().SetTransform<UIBoundableTransform>().Width = width;

    private static UIButtonComponent CreateToolButton(SceneNode parent, string text, string tooltip, float width, Action action)
    {
        var created = NativeUIElements.CreateButton(parent, text, _ => action(), width: width, height: ButtonHeight,
            margins: new Vector4(1.0f, 0.0f, 1.0f, 0.0f));
        created.Button.AccessibilityLabel = tooltip;
        return created.Button;
    }

    private static void PlayOrPause()
    {
        if (EditorState.IsTransitioning)
            return;
        if (EditorState.InEditMode)
            EditorState.RequestEnterPlayMode();
        else if (EditorState.IsPaused)
            EditorState.Resume();
        else if (EditorState.InPlayMode)
            EditorState.Pause();
    }

    /// <summary>Marks the current transform mode and space, and sets the play controls for the play state.</summary>
    private void UpdateState()
    {
        SetSelected(_translate, TransformTool3D.TransformMode == ETransformMode.Translate);
        SetSelected(_rotate, TransformTool3D.TransformMode == ETransformMode.Rotate);
        SetSelected(_scale, TransformTool3D.TransformMode == ETransformMode.Scale);
        SetSelected(_world, TransformTool3D.TransformSpace == ETransformSpace.World);
        SetSelected(_local, TransformTool3D.TransformSpace == ETransformSpace.Local);
        SetSelected(_parent, TransformTool3D.TransformSpace == ETransformSpace.Parent);
        SetSelected(_screen, TransformTool3D.TransformSpace == ETransformSpace.Screen);

        bool editing = EditorState.InEditMode;
        bool paused = EditorState.IsPaused;
        bool transitioning = EditorState.IsTransitioning;
        string playText = editing ? "Play" : paused ? "Resume" : "Pause";
        if (_playPauseLabel is { } label && label.Text != playText)
            label.Text = playText;
        SetEnabled(_playPause, !transitioning);
        SetEnabled(_stop, !transitioning && !editing);
        SetEnabled(_step, !transitioning && paused);
        string stateText = transitioning
            ? Engine.PlayMode.State == EPlayModeState.EnteringPlay ? "Starting..." : "Stopping..."
            : EditorState.InPlayMode ? "Playing" : paused ? "Paused" : string.Empty;
        if (_stateLabel is { } state && state.Text != stateText)
            state.Text = stateText;
    }

    private static void SetSelected(UIButtonComponent? button, bool selected)
    {
        if (button is not null && button.IsSelected != selected)
            button.IsSelected = selected;
    }

    private static void SetEnabled(UIButtonComponent? button, bool enabled)
    {
        if (button is not null && button.IsEnabled != enabled)
            button.IsEnabled = enabled;
    }
}

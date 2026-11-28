using System.Globalization;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.VR;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Components.Editing;

namespace XREngine.Editor.UI.Toolbar;

/// <summary>
/// The header row under the native main menu, like the ImGui editor toolbar. At the left are the transform mode
/// and space icon buttons and the snap toggle with its three snap steps. In the middle are the play controls with
/// the play state text. At the right are the OpenXR toggle with its status and desktop view, and the graphics API
/// selector.
/// </summary>
/// <remarks>
/// A late tick reads the transform tool, play mode, OpenXR, and graphics backend state each frame and updates the
/// buttons. The OpenXR runtime choice, the desktop view, and the API choice open in a shared popup menu.
/// </remarks>
[RequiresTransform(typeof(UIBoundableTransform))]
public sealed class EditorHeaderToolbarComponent : UIComponent
{
    private const float ButtonHeight = 26.0f;
    private const float IconButtonWidth = 28.0f;
    private const float IconSize = 18.0f;
    private const float SectionGap = 14.0f;

    private static readonly (EVrDesktopView View, string Label)[] DesktopViews =
    [
        (EVrDesktopView.FirstPerson, "First person"),
        (EVrDesktopView.Spectator, "Spectator"),
        (EVrDesktopView.EditorCamera, "Editor camera"),
        (EVrDesktopView.TextureOutput, "Spectator texture"),
    ];

    private UIButtonComponent? _translate, _rotate, _scale;
    private UIButtonComponent? _world, _local, _parent, _screen;
    private UIButtonComponent? _snap;
    private UIBoundableTransform? _snapValues;
    private UITextInputComponent? _snapTranslation, _snapRotation, _snapScale;
    private UIButtonComponent? _playPause, _stop, _step;
    private bool _playIconShowsPause;
    private UITextComponent? _stateLabel;
    private UIButtonComponent? _openXr;
    private UITextComponent? _openXrStatus;
    private UIBoundableTransform? _desktopViewTransform;
    private UITextComponent? _desktopViewLabel;
    private UIButtonComponent? _api;
    private UITextComponent? _apiLabel;
    private NativePropertyPopupHost? _popupHost;
    private UIContextMenuComponent? _popup;
    private bool _built;
    private bool _tickRegistered;
    /// <summary>The icon quad and icon name of each icon button, so a disabled button can dim its icon.</summary>
    private readonly Dictionary<UIButtonComponent, (UIMaterialComponent Icon, string Name)> _icons = [];

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
        _popup?.Hide();
        base.OnComponentDeactivated();
    }

    private void Build()
    {
        _built = true;
        SceneNode root = SceneNode;
        UIMaterialComponent background = root.AddComponent<UIMaterialComponent>()!;
        EditorGlassTheme.ApplyStatic(background, EditorGlassTheme.MenuBar);
        _popupHost = root.AddComponent<NativePropertyPopupHost>()!;

        SceneNode left = CreateRow(root, "HeaderTools", new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.0f), 720.0f);
        CreateSectionLabel(left, "Transform:", 66.0f);
        _translate = CreateIconButton(left, SvgEditorIcons.IconTranslate, "Translate (W)",
            () => TransformTool3D.TransformMode = ETransformMode.Translate);
        _rotate = CreateIconButton(left, SvgEditorIcons.IconRotate, "Rotate (E)",
            () => TransformTool3D.TransformMode = ETransformMode.Rotate);
        _scale = CreateIconButton(left, SvgEditorIcons.IconScale, "Scale (R)",
            () => TransformTool3D.TransformMode = ETransformMode.Scale);
        CreateGap(left, SectionGap);
        CreateSectionLabel(left, "Space:", 44.0f);
        _world = CreateIconButton(left, SvgEditorIcons.IconWorld, "World Space",
            () => TransformTool3D.TransformSpace = ETransformSpace.World);
        _local = CreateIconButton(left, SvgEditorIcons.IconLocal, "Local Space",
            () => TransformTool3D.TransformSpace = ETransformSpace.Local);
        _parent = CreateIconButton(left, SvgEditorIcons.IconParent, "Parent Space",
            () => TransformTool3D.TransformSpace = ETransformSpace.Parent);
        _screen = CreateIconButton(left, SvgEditorIcons.IconScreen, "Screen Space",
            () => TransformTool3D.TransformSpace = ETransformSpace.Screen);
        CreateGap(left, SectionGap);
        _snap = CreateIconButton(left, SvgEditorIcons.IconSnap, "Toggle Snapping",
            () => TransformTool3D.SnapEnabled = !TransformTool3D.SnapEnabled);
        CreateGap(left, 4.0f);
        SceneNode snapValues = left.NewChildWithTransform(out UIListTransform snapList, "SnapValues");
        snapList.DisplayHorizontal = true;
        snapList.ItemAlignment = EListAlignment.TopOrLeft;
        snapList.ItemSpacing = 4.0f;
        snapList.Width = 196.0f;
        _snapValues = snapList;
        _snapTranslation = CreateNumberField(snapValues, "T", 64.0f, () => TransformTool3D.TranslationSnapInterval,
            value => TransformTool3D.TranslationSnapInterval = value, "0.##");
        _snapRotation = CreateNumberField(snapValues, "R", 60.0f, () => TransformTool3D.RotationSnapInterval,
            value => TransformTool3D.RotationSnapInterval = value, "0.#");
        _snapScale = CreateNumberField(snapValues, "S", 60.0f, () => TransformTool3D.ScaleSnapInterval,
            value => TransformTool3D.ScaleSnapInterval = value, "0.##");

        // The play controls sit at the middle of the row, like the ImGui toolbar.
        SceneNode middle = CreateRow(root, "HeaderPlay", new Vector2(0.5f, 0.0f), new Vector2(0.5f, 0.0f), 200.0f);
        _playPause = CreateIconButton(middle, SvgEditorIcons.IconPlay, "Play (F5)", PlayOrPause);
        _stop = CreateIconButton(middle, SvgEditorIcons.IconStop, "Stop (Shift+F5)", EditorState.RequestExitPlayMode);
        _step = CreateIconButton(middle, SvgEditorIcons.IconStepFrame, "Step Frame (F6)", EditorState.StepFrame);
        SceneNode stateNode = middle.NewChild<UITextComponent>(out var state);
        stateNode.GetTransformAs<UIBoundableTransform>(true)!.Width = 80.0f;
        state.FontSize = EditorGlassTheme.CaptionFontSize;
        state.VerticalAlignment = EVerticalAlignment.Center;
        state.BoundableTransform.Margins = new Vector4(6.0f, 0.0f, 0.0f, 0.0f);
        _stateLabel = state;

        // The OpenXR and API controls sit at the right of the row.
        SceneNode right = CreateRow(root, "HeaderSession", new Vector2(1.0f, 0.0f), new Vector2(1.0f, 0.0f), 470.0f);
        var openXr = NativeUIElements.CreateButton(right, "OpenXR", _ => ToggleOpenXr(), width: 70.0f, height: ButtonHeight,
            margins: new Vector4(1.0f, 0.0f, 1.0f, 0.0f));
        _openXr = openXr.Button;
        _openXr.AccessibilityLabel = "OpenXR";
        SceneNode statusNode = right.NewChild<UITextComponent>(out var status);
        statusNode.GetTransformAs<UIBoundableTransform>(true)!.Width = 130.0f;
        status.FontSize = EditorGlassTheme.CaptionFontSize;
        status.Color = EditorGlassTheme.TextSecondary;
        status.VerticalAlignment = EVerticalAlignment.Center;
        status.ClipToBounds = true;
        status.BoundableTransform.Margins = new Vector4(6.0f, 0.0f, 4.0f, 0.0f);
        _openXrStatus = status;
        var desktopView = NativeUIElements.CreateButton(right, "Desktop view", button => ShowDesktopViewMenu(button),
            width: 120.0f, height: ButtonHeight, margins: new Vector4(1.0f, 0.0f, 1.0f, 0.0f));
        _desktopViewTransform = desktopView.Node.GetTransformAs<UIBoundableTransform>(true);
        _desktopViewLabel = desktopView.Label;
        CreateGap(right, 6.0f);
        CreateSectionLabel(right, "API:", 30.0f);
        var api = NativeUIElements.CreateButton(right, "Vulkan", button => ShowApiMenu(button), width: 92.0f,
            height: ButtonHeight, margins: new Vector4(1.0f, 0.0f, 1.0f, 0.0f));
        _api = api.Button;
        _apiLabel = api.Label;
    }

    /// <summary>Creates a horizontal row of controls that fills the header height and is anchored at <paramref name="anchor"/>.</summary>
    private static SceneNode CreateRow(SceneNode parent, string name, Vector2 anchor, Vector2 pivot, float width)
    {
        SceneNode row = parent.NewChildWithTransform(out UIListTransform list, name);
        list.DisplayHorizontal = true;
        list.ItemAlignment = EListAlignment.TopOrLeft;
        list.ItemSpacing = 2.0f;
        list.Padding = new Vector4(8.0f, 0.0f, 8.0f, 0.0f);
        list.MinAnchor = new Vector2(anchor.X, 0.0f);
        list.MaxAnchor = new Vector2(anchor.X, 1.0f);
        list.NormalizedPivot = pivot;
        list.Width = width;
        return row;
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

    /// <summary>Creates a square button that shows one editor icon, like an ImGui toolbar icon button.</summary>
    private UIButtonComponent CreateIconButton(SceneNode parent, string iconName, string tooltip, Action action)
    {
        var created = NativeUIElements.CreateButton(parent, string.Empty, _ => action(), width: IconButtonWidth,
            height: ButtonHeight, margins: new Vector4(1.0f, 0.0f, 1.0f, 0.0f));
        created.Button.AccessibilityLabel = tooltip;
        UIMaterialComponent icon = NativeEditorIcons.CreateIcon(created.Node, iconName, new Vector2(0.5f, 0.5f), Vector2.Zero, IconSize);
        _icons[created.Button] = (icon, iconName);
        return created.Button;
    }

    /// <summary>Shows <paramref name="iconName"/> on an icon button, dimmed while the button is disabled.</summary>
    private void SetButtonIcon(UIButtonComponent button, string iconName)
    {
        if (!_icons.TryGetValue(button, out var entry))
            return;
        _icons[button] = (entry.Icon, iconName);
        NativeEditorIcons.SetIcon(entry.Icon, iconName, button.IsEnabled ? ColorF4.White : DisabledIconTint);
    }

    private static readonly ColorF4 DisabledIconTint = new(1.0f, 1.0f, 1.0f, 0.35f);

    /// <summary>
    /// Creates a labeled number field, such as "T 1", that applies its value on Enter and shows the stored value
    /// again on Escape or a bad number.
    /// </summary>
    private static UITextInputComponent CreateNumberField(SceneNode parent, string prefix, float width,
        Func<float> read, Action<float> write, string format)
    {
        SceneNode node = parent.NewChild($"Snap{prefix}");
        UIBoundableTransform bounds = node.SetTransform<UIBoundableTransform>();
        bounds.Width = width;
        bounds.MinAnchor = new Vector2(0.0f, 0.5f);
        bounds.MaxAnchor = new Vector2(0.0f, 0.5f);
        bounds.NormalizedPivot = new Vector2(0.0f, 0.5f);
        bounds.Height = 22.0f;
        var (input, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(node,
            fontSize: EditorGlassTheme.CaptionFontSize, textMargins: new Vector4(18.0f, 1.0f, 4.0f, 1.0f));
        input.SingleLineMode = true;
        input.Text = read().ToString(format, CultureInfo.InvariantCulture);
        SceneNode prefixNode = node.NewChild<UITextComponent>(out var prefixLabel);
        prefixNode.GetTransformAs<UIBoundableTransform>(true)!.Margins = new Vector4(6.0f, 0.0f, 0.0f, 0.0f);
        prefixLabel.Text = prefix;
        prefixLabel.FontSize = EditorGlassTheme.CaptionFontSize;
        prefixLabel.Color = EditorGlassTheme.TextSecondary;
        prefixLabel.VerticalAlignment = EVerticalAlignment.Center;
        input.Submitted += field =>
        {
            if (float.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                float.IsFinite(value))
                write(value);
            field.Text = read().ToString(format, CultureInfo.InvariantCulture);
        };
        input.Cancelled += field => field.Text = read().ToString(format, CultureInfo.InvariantCulture);
        return input;
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

    private UIContextMenuComponent? GetPopup()
    {
        if (_popup is not null)
            return _popup;
        if (_popupHost is null || _popupHost.UITransform.GetCanvasTransform() is null)
            return null;
        _popup = _popupHost.TryCreatePopup(static () => true, out UIContextMenuComponent? created) ? created : null;
        return _popup;
    }

    /// <summary>Gets the canvas point under the left edge of <paramref name="control"/>, where its menu opens.</summary>
    private static Vector2 MenuPosition(UIInteractableComponent control)
        => control.BoundableTransform.LocalToCanvas(Vector2.Zero);

    /// <summary>
    /// Turns OpenXR off when it is on. When it is off, opens the runtime choice, like the ImGui runtime prompt.
    /// </summary>
    private void ToggleOpenXr()
    {
        if (!EditorOpenXrPawnSwitcher.CanToggle)
            return;
        if (EditorOpenXrPawnSwitcher.IsRequested)
        {
            EditorOpenXrPawnSwitcher.TrySetEnabled(false);
            return;
        }
        if (_openXr is null || GetPopup() is not { } popup)
            return;
        popup.Show(MenuPosition(_openXr),
            new ContextMenuItem("Monado (testing)",
                () => EditorOpenXrPawnSwitcher.TrySetEnabled(true, EditorOpenXrRuntimeChoice.Monado)),
            new ContextMenuItem("SteamVR (headset)",
                () => EditorOpenXrPawnSwitcher.TrySetEnabled(true, EditorOpenXrRuntimeChoice.SteamVR)));
    }

    private void ShowDesktopViewMenu(UIInteractableComponent button)
    {
        if (GetPopup() is not { } popup)
            return;
        EVrDesktopView current = EditorOpenXrPawnSwitcher.DesktopView;
        ContextMenuItem[] items = new ContextMenuItem[DesktopViews.Length];
        for (int index = 0; index < DesktopViews.Length; index++)
        {
            (EVrDesktopView view, string label) = DesktopViews[index];
            items[index] = new ContextMenuItem(view == current ? $"{label} (current)" : label,
                () => EditorOpenXrPawnSwitcher.SetDesktopView(view));
        }
        popup.Show(MenuPosition(button), items);
    }

    private void ShowApiMenu(UIInteractableComponent button)
    {
        if (!EditorGraphicsBackendSwitcher.CanSwitch || GetPopup() is not { } popup)
            return;
        ERenderLibrary active = EditorGraphicsBackendSwitcher.ActiveBackend;
        popup.Show(MenuPosition(button),
            new ContextMenuItem("OpenGL", () => EditorGraphicsBackendSwitcher.TrySwitchBackend(ERenderLibrary.OpenGL),
                active != ERenderLibrary.OpenGL),
            new ContextMenuItem("Vulkan", () => EditorGraphicsBackendSwitcher.TrySwitchBackend(ERenderLibrary.Vulkan),
                active != ERenderLibrary.Vulkan));
    }

    /// <summary>Reads the tool, play, OpenXR, and backend state and updates the header controls.</summary>
    private void UpdateState()
    {
        SetSelected(_translate, TransformTool3D.TransformMode == ETransformMode.Translate);
        SetSelected(_rotate, TransformTool3D.TransformMode == ETransformMode.Rotate);
        SetSelected(_scale, TransformTool3D.TransformMode == ETransformMode.Scale);
        SetSelected(_world, TransformTool3D.TransformSpace == ETransformSpace.World);
        SetSelected(_local, TransformTool3D.TransformSpace == ETransformSpace.Local);
        SetSelected(_parent, TransformTool3D.TransformSpace == ETransformSpace.Parent);
        SetSelected(_screen, TransformTool3D.TransformSpace == ETransformSpace.Screen);
        bool snap = TransformTool3D.SnapEnabled;
        SetSelected(_snap, snap);
        SetVisible(_snapValues, snap);

        bool editing = EditorState.InEditMode;
        bool paused = EditorState.IsPaused;
        bool transitioning = EditorState.IsTransitioning;
        bool showPause = !editing && !paused;
        if (_playPause is { } play && showPause != _playIconShowsPause)
        {
            _playIconShowsPause = showPause;
            SetButtonIcon(play, showPause ? SvgEditorIcons.IconPause : SvgEditorIcons.IconPlay);
            play.AccessibilityLabel = showPause ? "Pause" : editing ? "Play (F5)" : "Resume";
        }
        SetEnabled(_playPause, !transitioning);
        SetEnabled(_stop, !transitioning && !editing);
        SetEnabled(_step, !transitioning && paused);
        string stateText = transitioning
            ? Engine.PlayMode.State == EPlayModeState.EnteringPlay ? "Starting..." : "Stopping..."
            : EditorState.InPlayMode ? "Playing" : paused ? "Paused" : string.Empty;
        SetText(_stateLabel, stateText);

        bool openXr = EditorOpenXrPawnSwitcher.IsRequested;
        SetSelected(_openXr, openXr);
        SetEnabled(_openXr, EditorOpenXrPawnSwitcher.CanToggle);
        SetText(_openXrStatus, EditorOpenXrPawnSwitcher.Status);
        SetVisible(_desktopViewTransform, openXr);
        if (openXr)
        {
            EVrDesktopView view = EditorOpenXrPawnSwitcher.DesktopView;
            foreach ((EVrDesktopView option, string label) in DesktopViews)
                if (option == view)
                    SetText(_desktopViewLabel, label);
        }

        SetEnabled(_api, EditorGraphicsBackendSwitcher.CanSwitch);
        SetText(_apiLabel, EditorGraphicsBackendSwitcher.IsSwitching
            ? "Switching..."
            : EditorGraphicsBackendSwitcher.ActiveBackend.ToString());
    }

    private static void SetSelected(UIButtonComponent? button, bool selected)
    {
        if (button is not null && button.IsSelected != selected)
            button.IsSelected = selected;
    }

    private void SetEnabled(UIButtonComponent? button, bool enabled)
    {
        if (button is null || button.IsEnabled == enabled)
            return;
        button.IsEnabled = enabled;
        // An icon button dims its icon while it is disabled, as a text button dims its label.
        if (_icons.TryGetValue(button, out var entry))
            SetButtonIcon(button, entry.Name);
    }

    private static void SetText(UITextComponent? label, string text)
    {
        if (label is not null && label.Text != text)
            label.Text = text;
    }

    private static void SetVisible(UIBoundableTransform? transform, bool visible)
    {
        EVisibility visibility = visible ? EVisibility.Visible : EVisibility.Collapsed;
        if (transform is not null && transform.Visibility != visibility)
            transform.Visibility = visibility;
    }
}

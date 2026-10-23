using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Components.Editing;
using System.Numerics;

namespace XREngine.Editor;

public class SceneViewPanel : EditorPanel
{
    private UIViewportComponent? _sceneViewport;
    private UIListTransform? _toolbar;
    private NativeSceneViewportInput? _sceneInput;
    private UIButtonComponent? _translateButton;
    private UIButtonComponent? _rotateButton;
    private UIButtonComponent? _scaleButton;
    private UIButtonComponent? _worldButton;
    private UIButtonComponent? _localButton;
    private UIButtonComponent? _parentButton;
    private UIButtonComponent? _screenButton;

    public XRCamera? Camera => CameraComponent?.Camera;
    internal XRViewport? SceneViewport => _sceneViewport?.Viewport;
    public CameraComponent? CameraComponent { get; set; }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _sceneViewport ??= SceneNode.AddComponent<UIViewportComponent>();
        _sceneInput ??= SceneNode.AddComponent<NativeSceneViewportInput>();
        if (_sceneInput is not null)
            _sceneInput.Panel = this;
        CreateToolbar();
        BindCamera();
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateSceneControls);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateSceneControls);
        base.OnComponentDeactivated();
    }

    private void UpdateSceneControls()
    {
        BindCamera();
        if (_translateButton is not null) _translateButton.IsSelected = TransformTool3D.TransformMode == ETransformMode.Translate;
        if (_rotateButton is not null) _rotateButton.IsSelected = TransformTool3D.TransformMode == ETransformMode.Rotate;
        if (_scaleButton is not null) _scaleButton.IsSelected = TransformTool3D.TransformMode == ETransformMode.Scale;
        if (_worldButton is not null) _worldButton.IsSelected = TransformTool3D.TransformSpace == ETransformSpace.World;
        if (_localButton is not null) _localButton.IsSelected = TransformTool3D.TransformSpace == ETransformSpace.Local;
        if (_parentButton is not null) _parentButton.IsSelected = TransformTool3D.TransformSpace == ETransformSpace.Parent;
        if (_screenButton is not null) _screenButton.IsSelected = TransformTool3D.TransformSpace == ETransformSpace.Screen;
    }

    private void BindCamera()
    {
        if (_sceneViewport is null)
            return;
        if (Window is not null && !ReferenceEquals(_sceneViewport.Viewport.Window, Window))
            _sceneViewport.Viewport.Window = Window;
        XRViewport? source = Window?.Viewports.FirstOrDefault() ?? RuntimeEngine.Windows.FirstOrDefault()?.Viewports.FirstOrDefault();
        CameraComponent? camera = CameraComponent is { SceneNode: not null } selected
            ? selected : source?.CameraComponent;
        if (!ReferenceEquals(_sceneViewport.Viewport.CameraComponent, camera))
            _sceneViewport.Viewport.CameraComponent = camera;
        if (source?.WorldInstanceOverride is { } world && !ReferenceEquals(_sceneViewport.Viewport.WorldInstanceOverride, world))
            _sceneViewport.Viewport.WorldInstanceOverride = world;
    }

    private void CreateToolbar()
    {
        if (_toolbar is not null)
            return;
        SceneNode toolbarNode = SceneNode.NewChildWithTransform(out UIListTransform toolbar, "SceneCameraToolbar");
        toolbar.MinAnchor = new Vector2(0, 1);
        toolbar.MaxAnchor = new Vector2(0, 1);
        toolbar.NormalizedPivot = new Vector2(0, 1);
        toolbar.Width = 236;
        toolbar.Height = 96;
        toolbar.DisplayHorizontal = false;
        toolbar.ItemAlignment = EListAlignment.TopOrLeft;
        toolbar.ItemSize = 28;
        toolbar.ItemSpacing = 2;
        SceneNode cameraRow = CreateToolbarRow(toolbarNode, 114);
        NativeUIElements.CreateButton(cameraRow, "Scene Camera", _ => { CameraComponent = null; BindCamera(); }, width: 114);
        NativeUIElements.CreateButton(cameraRow, "Selected Camera", _ =>
        {
            CameraComponent = Selection.SceneNode?.GetComponent<CameraComponent>();
            BindCamera();
        }, width: 114);
        SceneNode modeRow = CreateToolbarRow(toolbarNode, 74);
        _translateButton = NativeUIElements.CreateButton(modeRow, "Move (W)", _ => TransformTool3D.TransformMode = ETransformMode.Translate, width: 74).Button;
        _rotateButton = NativeUIElements.CreateButton(modeRow, "Rotate (E)", _ => TransformTool3D.TransformMode = ETransformMode.Rotate, width: 74).Button;
        _scaleButton = NativeUIElements.CreateButton(modeRow, "Scale (R)", _ => TransformTool3D.TransformMode = ETransformMode.Scale, width: 74).Button;
        SceneNode spaceRow = CreateToolbarRow(toolbarNode, 54);
        _worldButton = NativeUIElements.CreateButton(spaceRow, "World", _ => TransformTool3D.TransformSpace = ETransformSpace.World, width: 54).Button;
        _localButton = NativeUIElements.CreateButton(spaceRow, "Local", _ => TransformTool3D.TransformSpace = ETransformSpace.Local, width: 54).Button;
        _parentButton = NativeUIElements.CreateButton(spaceRow, "Parent", _ => TransformTool3D.TransformSpace = ETransformSpace.Parent, width: 54).Button;
        _screenButton = NativeUIElements.CreateButton(spaceRow, "Screen", _ => TransformTool3D.TransformSpace = ETransformSpace.Screen, width: 54).Button;
        _toolbar = toolbar;
        UpdateSceneControls();
    }

    private static SceneNode CreateToolbarRow(SceneNode parent, float itemWidth)
    {
        SceneNode row = parent.NewChildWithTransform(out UIListTransform list, "SceneToolbarRow");
        list.DisplayHorizontal = true;
        list.ItemAlignment = EListAlignment.TopOrLeft;
        list.ItemSize = itemWidth;
        list.ItemSpacing = 2;
        return row;
    }
}

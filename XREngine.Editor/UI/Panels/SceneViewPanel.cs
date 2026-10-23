using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using System.Numerics;

namespace XREngine.Editor;

public class SceneViewPanel : EditorPanel
{
    private UIViewportComponent? _sceneViewport;
    private UIListTransform? _toolbar;

    public XRCamera? Camera => CameraComponent?.Camera;
    public CameraComponent? CameraComponent { get; set; }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _sceneViewport ??= SceneNode.AddComponent<UIViewportComponent>();
        CreateToolbar();
        BindCamera();
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, BindCamera);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, BindCamera);
        base.OnComponentDeactivated();
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
        toolbar.Height = 32;
        toolbar.DisplayHorizontal = true;
        toolbar.ItemAlignment = EListAlignment.TopOrLeft;
        toolbar.ItemSize = 114;
        toolbar.ItemSpacing = 4;
        NativeUIElements.CreateButton(toolbarNode, "Scene Camera", _ => { CameraComponent = null; BindCamera(); }, width: 114);
        NativeUIElements.CreateButton(toolbarNode, "Selected Camera", _ =>
        {
            CameraComponent = Selection.SceneNode?.GetComponent<CameraComponent>();
            BindCamera();
        }, width: 114);
        _toolbar = toolbar;
    }
}

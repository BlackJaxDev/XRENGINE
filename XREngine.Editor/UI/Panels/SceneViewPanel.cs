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

    private CameraComponent? _cameraComponent;
    private bool _bindingCamera;
    private bool _cameraBindingStopped = true;
    private bool _cameraTerminal;
    private ulong _cameraRequestRevision;

    public XRCamera? Camera => SceneViewport?.ActiveCamera;
    internal XRViewport? SceneViewport => _sceneViewport?.Viewport;
    public CameraComponent? CameraComponent
    {
        get => _cameraComponent;
        set
        {
            if (_cameraTerminal || IsDestroyed || IsDestroyQueued || ReferenceEquals(_cameraComponent, value))
                return;
            _cameraRequestRevision++;
            if (SetField(ref _cameraComponent, value))
                BindCamera();
        }
    }

    protected override bool OnPropertyChanging<T>(string? propName, T field, T value)
    {
        ulong revision = _cameraRequestRevision;
        bool allowed = base.OnPropertyChanging(propName, field, value);
        return allowed && (propName != nameof(CameraComponent) ||
            !_cameraTerminal && !IsDestroyed && !IsDestroyQueued && revision == _cameraRequestRevision);
    }

    protected override void OnComponentActivated()
    {
        if (_cameraTerminal)
            return;
        _cameraBindingStopped = false;
        base.OnComponentActivated();
        if (_cameraBindingStopped || IsDestroyed || IsDestroyQueued)
            return;
        _sceneViewport ??= SceneNode.AddComponent<UIViewportComponent>();
        _sceneInput ??= SceneNode.AddComponent<NativeSceneViewportInput>();
        if (_sceneInput is not null)
            _sceneInput.Panel = this;
        CreateToolbar();
        BindCamera();
        if (!_cameraBindingStopped && !IsDestroyed && !IsDestroyQueued)
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateSceneControls);
    }

    protected override void OnComponentDeactivated()
    {
        _cameraBindingStopped = true;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateSceneControls);
        try { base.OnComponentDeactivated(); }
        finally { ClearSceneCamera(); }
    }

    protected override void OnDestroying()
    {
        _cameraTerminal = true;
        _cameraBindingStopped = true;
        SetField(ref _cameraComponent, null, publishNotifications: false);
        if (_sceneInput is not null && ReferenceEquals(_sceneInput.Panel, this))
            _sceneInput.Panel = null;
        try { ClearSceneCamera(); }
        finally { base.OnDestroying(); }
    }

    private void ClearSceneCamera()
    {
        if (_sceneViewport is not { IsDestroyed: false } surface)
            return;
        bool wasBinding = _bindingCamera;
        _bindingCamera = true;
        try { ClearViewportCamera(surface.Viewport); }
        finally { _bindingCamera = wasBinding; }
    }

    /// <summary>Clears independent viewport owners even if one notification fails.</summary>
    internal static void ClearViewportCamera(XRViewport viewport)
        => viewport.DetachSceneBinding();

    /// <summary>Rejects callback replacement of the exact viewport binding.</summary>
    internal static bool TryBindOwnedCamera(XRViewport target, XRWindow? window,
        IRuntimeRenderWorld? world, CameraComponent? camera)
    {
        ClearViewportCamera(target);
        try
        {
            target.Window = window;
            target.WorldInstanceOverride = world;
            target.CameraComponent = camera;
            if (ReferenceEquals(target.Window, window) &&
                ReferenceEquals(target.WorldInstanceOverride, world) &&
                ReferenceEquals(target.CameraComponent, camera) &&
                ReferenceEquals(target.Camera, camera?.Camera) &&
                (camera is null || CameraBelongsToWorld(camera, world)))
                return true;
        }
        catch
        {
            try { ClearViewportCamera(target); }
            catch { /* Preserve the binding failure after attempting all cleanup. */ }
            throw;
        }
        ClearViewportCamera(target);
        return false;
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
        if (_bindingCamera || _cameraBindingStopped || _cameraTerminal ||
            _sceneViewport is not { IsDestroyed: false } || IsDestroyed || IsDestroyQueued)
            return;
        _bindingCamera = true;
        try
        {
            XRViewport target = _sceneViewport.Viewport;
            XRWindow? window = Window;
            IRuntimeRenderWorld? world = window?.TargetWorldInstance;
            CameraComponent? requested = CameraComponent;
            CameraComponent? camera = ResolveOwnedCamera(window, world,
                window?.Viewports, requested);
            if (requested is not null && !CameraBelongsToWorld(requested, world))
            {
                CameraComponent = null;
                if (!ReferenceEquals(Window, window) || CameraComponent is not null)
                {
                    ClearSceneCamera();
                    return;
                }
            }
            CameraComponent? admittedRequest = CameraComponent;
            if (ReferenceEquals(target.Window, window) &&
                ReferenceEquals(target.WorldInstanceOverride, world) &&
                ReferenceEquals(target.CameraComponent, camera) &&
                ReferenceEquals(target.Camera, camera?.Camera))
                return;

            if (!TryBindOwnedCamera(target, window, world, camera))
                return;
            if (!ReferenceEquals(Window, window) ||
                !ReferenceEquals(window?.TargetWorldInstance, world) ||
                !ReferenceEquals(CameraComponent, admittedRequest) ||
                _cameraBindingStopped || _cameraTerminal || IsDestroyed || IsDestroyQueued ||
                camera is not null && !CameraBelongsToWorld(camera, world))
                ClearSceneCamera();
        }
        finally
        {
            _bindingCamera = false;
        }
    }

    /// <summary>Selects a camera from the panel's exact window and runtime world.</summary>
    internal static CameraComponent? ResolveOwnedCamera(XRWindow? window,
        IRuntimeRenderWorld? world, IReadOnlyList<XRViewport>? sources,
        CameraComponent? requested)
    {
        if (world is null || world.WorldContext is RuntimeWorld { IsDisposing: true })
            return null;
        if (CameraBelongsToWorld(requested, world))
            return requested;
        if (sources is null)
            return null;
        for (int index = 0; index < sources.Count; index++)
        {
            XRViewport source = sources[index];
            if (ReferenceEquals(source.Window, window) && ReferenceEquals(source.World, world) &&
                CameraBelongsToWorld(source.CameraComponent, world))
                return source.CameraComponent;
        }
        return null;
    }

    private static bool CameraBelongsToWorld(CameraComponent? camera, IRuntimeRenderWorld? world)
        => world is not null && camera is { IsDestroyed: false, IsDestroyQueued: false,
            SceneNode: { IsDestroyed: false, IsDestroyQueued: false } node } &&
            ReferenceEquals(node.World, world.WorldContext);

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

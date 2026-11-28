using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Components.Editing;
using System.Numerics;
using System.Runtime.ExceptionServices;

namespace XREngine.Editor;

public class SceneViewPanel : EditorPanel
{
    private UIViewportComponent? _sceneViewport;
    private UIListTransform? _toolbar;
    private NativeSceneViewportInput? _sceneInput;

    private CameraComponent? _cameraComponent;
    private bool _bindingCamera;
    private bool _cameraBindingStopped = true;
    private bool _cameraTerminal;
    private ulong _cameraRequestRevision;
    private bool _sceneControlsTickRegistered;

    public XRCamera? Camera => SceneViewport?.ActiveCamera;

    /// <summary>
    /// Gets whether the scene renders inside this panel. This is true when the editor preference
    /// <see cref="EditorPreferences.ViewportPresentationMode"/> is
    /// <see cref="EditorPreferences.EViewportPresentationMode.UseViewportPanel"/>. Otherwise the window
    /// viewport shows the scene across the whole window, behind the panels, and this panel is a clear
    /// region that forwards pointer input to that viewport. The ImGui editor uses the same preference.
    /// </summary>
    public static bool IsSceneDockedInPanel
        => Engine.EditorPreferences?.ViewportPresentationMode == EditorPreferences.EViewportPresentationMode.UseViewportPanel;

    /// <summary>
    /// Gets the viewport that shows the scene for this panel: the embedded viewport when the scene is docked
    /// in the panel, or the window viewport that renders the scene behind the editor UI.
    /// </summary>
    internal XRViewport? SceneViewport => _sceneViewport is { } docked
        ? docked.Viewport
        : ResolveWindowSceneViewport(Window);

    /// <summary>Reports whether <paramref name="viewport"/> is this panel's embedded scene viewport.</summary>
    internal bool IsDockedSceneViewport(XRViewport viewport)
        => _sceneViewport is { } docked && ReferenceEquals(docked.Viewport, viewport);

    /// <summary>Returns the first viewport of <paramref name="window"/> that renders a camera of the window's world.</summary>
    internal static XRViewport? ResolveWindowSceneViewport(XRWindow? window)
    {
        if (window?.TargetWorldInstance is not { } world)
            return null;
        IReadOnlyList<XRViewport> viewports = window.Viewports;
        for (int index = 0; index < viewports.Count; index++)
        {
            XRViewport viewport = viewports[index];
            if (ReferenceEquals(viewport.World, world) && CameraBelongsToWorld(viewport.CameraComponent, world))
                return viewport;
        }
        return null;
    }
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
        if (_sceneViewport is null && IsSceneDockedInPanel)
        {
            _sceneViewport = SceneNode.AddComponent<UIViewportComponent>();
            // The Scene panel is the editor's main 3D view, so it renders as a desktop scene output.
            if (_sceneViewport is not null)
                _sceneViewport.IsPrimarySceneView = true;
        }
        _sceneInput ??= SceneNode.AddComponent<NativeSceneViewportInput>();
        if (_sceneInput is not null)
            _sceneInput.Panel = this;
        CreateToolbar();
        BindCamera();
        if (!_cameraBindingStopped && !IsDestroyed && !IsDestroyQueued && !_sceneControlsTickRegistered)
        {
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateSceneControls);
            _sceneControlsTickRegistered = true;
        }
    }

    protected override void OnComponentDeactivated()
    {
        _cameraBindingStopped = true;
        UnregisterSceneControlsTick();
        try { base.OnComponentDeactivated(); }
        finally { ClearSceneCamera(); }
    }

    protected override void OnDestroying()
    {
        _cameraTerminal = true;
        _cameraBindingStopped = true;
        List<Exception>? failures = null;
        AttemptSceneCleanup(UnregisterSceneControlsTick, ref failures);
        AttemptSceneCleanup(() => SetField(ref _cameraComponent, null, publishNotifications: false), ref failures);
        AttemptSceneCleanup(() =>
        {
            if (_sceneInput is not null && ReferenceEquals(_sceneInput.Panel, this))
                _sceneInput.Panel = null;
        }, ref failures);
        AttemptSceneCleanup(ClearSceneCamera, ref failures);
        try { base.OnDestroying(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 })
            throw new AggregateException("Scene view cleanup failed.", failures);
    }

    private static void AttemptSceneCleanup(Action release, ref List<Exception>? failures)
    {
        try { release(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
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

    private void UnregisterSceneControlsTick()
    {
        if (!_sceneControlsTickRegistered) return;
        _sceneControlsTickRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateSceneControls);
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
        if (_cameraTerminal || _cameraBindingStopped) return;
        BindCamera();
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

    internal static bool CameraBelongsToWorld(CameraComponent? camera, IRuntimeRenderWorld? world)
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
        toolbar.Height = 28;
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
        // The transform mode and space buttons are in the editor header row, like the ImGui toolbar.
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

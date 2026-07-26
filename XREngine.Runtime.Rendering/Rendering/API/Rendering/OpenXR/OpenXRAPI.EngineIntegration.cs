using Silk.NET.OpenXR;
using XREngine;
using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    #region Public API + window binding

    /// <summary>
    /// Gets the OpenXR API instance.
    /// </summary>
    public XR Api { get; private set; }

    /// <summary>
    /// Gets or sets the window associated with this XR session.
    /// Setting a new window triggers initialization or cleanup as appropriate.
    /// </summary>
    public XRWindow? Window
    {
        get => _window;
        set => SetField(ref _window, value);
    }

    /// <summary>
    /// Called before a property changes to perform any necessary cleanup.
    /// </summary>
    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool change = base.OnPropertyChanging(propName, field, @new);
        if (change)
        {
            switch (propName)
            {
                case nameof(Window):
                    if (field is not null)
                        CleanUp();
                    break;
            }
        }
        return change;
    }

    /// <summary>
    /// Called after a property changes to perform any necessary initialization.
    /// </summary>
    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(Window):
                if (field is not null)
                    Initialize();
                break;
        }
    }

    /// <summary>
    /// Initializes the OpenXR session and associated resources.
    /// </summary>
    protected void Initialize()
    {
        EnableRuntimeMonitoring();
    }

    private void HookEngineTimerEvents()
    {
        // Hooking is owned by RuntimeEngine.VRState to ensure OpenVR/OpenXR share the same engine callback entrypoints.
    }

    private void UnhookEngineTimerEvents()
    {
        // Hooking is owned by RuntimeEngine.VRState to ensure OpenVR/OpenXR share the same engine callback entrypoints.
    }

    private XRViewport? TryGetSourceViewport(out XRCamera? sourceCamera)
        => TryGetSourceViewport(out sourceCamera, out _);

    private XRViewport? TryGetSourceViewport(out XRCamera? sourceCamera, out IRuntimeRenderWorld? sourceWorld)
    {
        sourceCamera = null;
        sourceWorld = null;
        if (Window is null)
            return null;

        foreach (var vp in Window.Viewports)
        {
            if (TryResolveSourceViewport(vp, out sourceCamera, out sourceWorld))
                return vp;
        }

        foreach (var player in RuntimeRenderingHostServices.Factories.EnumerateLocalPlayers())
        {
            if (TryResolveSourcePlayer(player, Window.TargetWorldInstance, out XRViewport? playerViewport, out sourceCamera, out sourceWorld))
                return playerViewport;
        }

        if (TryResolveWorldCamera(Window.TargetWorldInstance, out CameraComponent? worldCameraComponent) &&
            worldCameraComponent is not null)
        {
            sourceCamera = worldCameraComponent.Camera;
            sourceWorld = worldCameraComponent.SceneNode?.World as IRuntimeRenderWorld
                          ?? Window.TargetWorldInstance;

            foreach (var vp in Window.Viewports)
            {
                if (vp is null)
                    continue;

                if (!ReferenceEquals(vp.CameraComponent, worldCameraComponent))
                {
                    vp.CameraComponent = worldCameraComponent;
                    vp.EnsureViewportBoundToCamera();
                }

                return vp;
            }

            return null;
        }

        sourceWorld = Window.TargetWorldInstance;
        return null;
    }

    private static bool TryResolveSourceViewport(
        XRViewport? viewport,
        out XRCamera? sourceCamera,
        out IRuntimeRenderWorld? sourceWorld)
    {
        sourceCamera = null;
        sourceWorld = null;
        if (viewport is null)
            return false;

        // Resolve first: editor/play-mode bootstrap often attaches CameraComponent from
        // the associated pawn lazily, so checking viewport.World before this can skip
        // the exact refresh that makes the world available.
        sourceCamera = viewport.ResolveActiveCameraForExternalRenderSource();
        sourceWorld = viewport.World ?? viewport.Window?.TargetWorldInstance;
        return sourceCamera is not null && sourceWorld is not null;
    }

    private static bool TryResolveSourcePlayer(
        IPawnController? player,
        IRuntimeRenderWorld? fallbackWorld,
        out XRViewport? sourceViewport,
        out XRCamera? sourceCamera,
        out IRuntimeRenderWorld? sourceWorld)
    {
        sourceViewport = null;
        sourceCamera = null;
        sourceWorld = null;
        if (player is null)
            return false;

        if (player.Viewport is XRViewport viewport)
        {
            sourceViewport = viewport;
            if (TryResolveSourceViewport(viewport, out sourceCamera, out sourceWorld))
                return true;
        }

        if (player.ControlledPawnComponent is not IRuntimeInputControllablePawn pawn ||
            pawn.RuntimeCameraComponent is not CameraComponent cameraComponent)
        {
            return false;
        }

        sourceCamera = cameraComponent.Camera;
        sourceWorld = cameraComponent.SceneNode?.World as IRuntimeRenderWorld
                      ?? sourceViewport?.World
                      ?? fallbackWorld;
        if (sourceCamera is null || sourceWorld is null)
            return false;

        if (sourceViewport is not null && !ReferenceEquals(sourceViewport.CameraComponent, cameraComponent))
        {
            sourceViewport.CameraComponent = cameraComponent;
            sourceViewport.EnsureViewportBoundToCamera();
        }

        return true;
    }

    private static bool TryResolveWorldCamera(
        IRuntimeRenderWorld? world,
        out CameraComponent? cameraComponent)
    {
        cameraComponent = null;
        if (world is null)
            return false;

        foreach (var root in world.RootNodes)
        {
            var candidate = root.FindFirstDescendantComponent<CameraComponent>();
            if (candidate?.Camera is null ||
                candidate.IsDestroyed ||
                candidate.SceneNode is null ||
                candidate.SceneNode.IsDestroyed)
            {
                continue;
            }

            cameraComponent = candidate;
            return true;
        }

        return false;
    }

    private XRViewport? TryGetSourceViewport()
        => TryGetSourceViewport(out _);

    #endregion
}

using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Required renderer, render-pipeline, window, and editor-panel factories and lifecycle hooks.
/// </summary>
public interface IRuntimeRendererFactoryServices
{
    /// <summary>
    /// Resolves the graphics API backend for a specific host window.
    /// </summary>
    RuntimeGraphicsApiKind GetWindowRenderBackend(IRuntimeRenderWindowHost? window);

    /// <summary>
    /// Enumerates viewports that are currently active in host windows or XR presentation.
    /// </summary>
    IEnumerable<IRuntimeViewportHost> EnumerateActiveViewports();

    /// <summary>
    /// Enumerates local player controllers known to the host.
    /// </summary>
    IEnumerable<IPawnController> EnumerateLocalPlayers();

    /// <summary>
    /// Resolves the default depth mode used when a scene camera is initialized by runtime rendering code.
    /// </summary>
    XRCamera.EDepthMode ResolveSceneCameraDepthModePreference();

    /// <summary>
    /// Ensures a controllable pawn exists for the supplied camera and local player.
    /// </summary>
    IRuntimeInputControllablePawn? EnsurePawnForCamera(
        SceneNode sceneNode,
        CameraComponent camera,
        ELocalPlayerIndex playerIndex,
        Type? pawnType = null);

    /// <summary>
    /// Schedules a viewport-space physics pick and invokes the callback with sorted hit results.
    /// </summary>
    void PickViewportPhysicsAsync(
        XRViewport viewport,
        CameraComponent camera,
        Vector2 normalizedViewportPosition,
        LayerMask layerMask,
        object? filter,
        SortedDictionary<float, List<(XRComponent? item, object? data)>> orderedPhysicsResults,
        Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>?> physicsFinishedCallback,
        bool useUnjitteredProjection);

    /// <summary>
    /// Creates the host's default render pipeline implementation.
    /// </summary>
    IRuntimeRenderPipelineHost? CreateDefaultRenderPipeline();

    /// <summary>
    /// Gets the renderer backend modules installed by this application's composition root.
    /// Hosts without renderer support expose an unconfigured catalog whose required lookups
    /// fail with actionable diagnostics.
    /// </summary>
    IRendererBackendCatalog RendererBackends => RendererBackendCatalog.Unconfigured;

    /// <summary>
    /// Creates a backend renderer for the supplied host window and graphics API.
    /// New stable call sites should resolve through <see cref="RendererBackends"/>.
    /// </summary>
    IRuntimeRendererHost CreateRenderer(IRuntimeRenderWindowHost window, RuntimeGraphicsApiKind apiKind);

    /// <summary>
    /// Creates the adapter used to present a window render target inside an editor scene panel.
    /// </summary>
    IRuntimeWindowScenePanelAdapter CreateWindowScenePanelAdapter();

    /// <summary>
    /// Gets the scene panel subregion that should receive window rendering, when panel mode is active.
    /// </summary>
    BoundingRectangle? GetScenePanelRenderRegion(IRuntimeRenderWindowHost window);

    /// <summary>
    /// Returns whether the host allows the supplied render window to close.
    /// </summary>
    bool AllowWindowClose(IRuntimeRenderWindowHost window);

    /// <summary>
    /// Stops host work that can produce or mutate render resources before the supplied window tears its renderer down.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when renderer teardown can proceed safely; otherwise <see langword="false"/>
    /// and the window will abandon process-exit cleanup rather than race active host work.
    /// </returns>
    bool QuiesceForWindowRendererTeardown(IRuntimeRenderWindowHost window) => true;

    /// <summary>
    /// Removes the supplied render window from the host window collection.
    /// </summary>
    void RemoveWindow(IRuntimeRenderWindowHost window);

    /// <summary>
    /// Replicates a window target-world change through the host networking layer, when appropriate.
    /// </summary>
    void ReplicateWindowTargetWorldChange(IRuntimeRenderWindowHost window);
}

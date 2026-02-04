using Extensions;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Input;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Picking;
using XREngine.Rendering.UI;
using XREngine.Scene;
using YamlDotNet.Serialization;
using State = XREngine.Engine.Rendering.State;

namespace XREngine.Rendering
{
    /// <summary>
    /// Defines a rectangular area to render to.
    /// Can either be a window or render texture.
    /// </summary>
    [RuntimeOnly]
    public sealed class XRViewport : XRBase
    {
        #region Fields

        /// <summary>
        /// The standalone camera instance used for rendering when no CameraComponent is assigned.
        /// This allows the viewport to render without requiring a full scene graph hierarchy.
        /// </summary>
        private XRCamera? _camera = null;

        /// <summary>
        /// The camera component from the scene graph that provides both the camera and its transform.
        /// When set, this takes precedence over the standalone _camera field for determining the ActiveCamera.
        /// </summary>
        private CameraComponent? _cameraComponent = null;

        /// <summary>
        /// The local player controller associated with this viewport.
        /// Used to link input handling and player-specific rendering (e.g., split-screen scenarios).
        /// </summary>
        private LocalPlayerController? _associatedPlayer = null;

        /// <summary>
        /// The screen-space rectangular region where this viewport renders within the parent window.
        /// Coordinates are in pixels, with (0,0) at the bottom-left corner of the window.
        /// </summary>
        private BoundingRectangle _region = new();

        /// <summary>
        /// The internal rendering resolution, which may differ from the viewport's display resolution.
        /// Used for resolution scaling (e.g., rendering at lower resolution then upscaling for performance).
        /// </summary>
        private BoundingRectangle _internalResolutionRegion = new();

        /// <summary>
        /// The render pipeline instance that manages the rendering process for this viewport.
        /// Contains the mesh render commands, framebuffers, and other rendering state.
        /// </summary>
        private readonly XRRenderPipelineInstance _renderPipeline = new();

        /// <summary>
        /// When true, the render pipeline is automatically updated to match the camera's RenderPipeline property.
        /// Set to false if you want to use a custom pipeline independent of the camera.
        /// </summary>
        private bool _setRenderPipelineFromCamera = true;

        /// <summary>
        /// When true, visible objects are automatically collected each frame via the engine's timer events.
        /// Set to false for manual control over when visibility collection occurs (e.g., for deferred rendering setups).
        /// </summary>
        private bool _automaticallyCollectVisible = true;

        /// <summary>
        /// When true, render command buffers are automatically double-buffered (swapped) each frame.
        /// Set to false for manual control over buffer swapping (e.g., for multi-pass rendering).
        /// </summary>
        private bool _automaticallySwapBuffers = true;

        /// <summary>
        /// When true, screen-space UI overlays (HUD, menus) are rendered on top of the 3D scene.
        /// Set to false to disable UI rendering for this viewport (e.g., for reflection cameras).
        /// </summary>
        private bool _allowUIRender = true;

        /// <summary>
        /// When true, objects outside the camera's view frustum are culled (not rendered).
        /// Set to false to disable frustum culling (e.g., for shadow map rendering or omnidirectional cameras).
        /// </summary>
        private bool _cullWithFrustum = true;

        /// <summary>
        /// The rendering order index for this viewport. Lower indices render first.
        /// Used for layered viewport rendering where later viewports render on top of earlier ones.
        /// </summary>
        private int _index = 0;

        /// <summary>
        /// The left edge of the viewport as a percentage (0.0-1.0) of the parent window's width.
        /// 0.0 = left edge of window, 1.0 = right edge of window.
        /// </summary>
        public float _leftPercentage = 0.0f;

        /// <summary>
        /// The right edge of the viewport as a percentage (0.0-1.0) of the parent window's width.
        /// 0.0 = left edge of window, 1.0 = right edge of window.
        /// </summary>
        public float _rightPercentage = 1.0f;

        /// <summary>
        /// The bottom edge of the viewport as a percentage (0.0-1.0) of the parent window's height.
        /// 0.0 = bottom edge of window, 1.0 = top edge of window.
        /// </summary>
        public float _bottomPercentage = 0.0f;

        /// <summary>
        /// The top edge of the viewport as a percentage (0.0-1.0) of the parent window's height.
        /// 0.0 = bottom edge of window, 1.0 = top edge of window.
        /// </summary>
        public float _topPercentage = 1.0f;

        #endregion

        #region Events

        /// <summary>
        /// Raised when the viewport's display region (screen-space dimensions) changes.
        /// Subscribe to this event to update UI layouts or other resolution-dependent resources.
        /// The viewport instance is passed as a parameter for convenience.
        /// </summary>
        public event Action<XRViewport>? Resized;

        /// <summary>
        /// Raised when the viewport's internal rendering resolution changes.
        /// This may differ from the display resolution when using resolution scaling.
        /// Subscribe to update render targets or other internal-resolution-dependent resources.
        /// </summary>
        public event Action<XRViewport>? InternalResolutionResized;

        #endregion

        #region Properties

        /// <summary>
        /// The parent window that contains this viewport.
        /// A window can contain multiple viewports for split-screen or picture-in-picture rendering.
        /// May be null for off-screen render targets that don't display to a window.
        /// </summary>
        public XRWindow? Window { get; set; }

        /// <summary>
        /// Optional override for the world instance to render.
        /// When set, this takes precedence over the camera component's world instance.
        /// Use this when you need to render a different world than where the camera component lives,
        /// such as rendering a preview of another level or a minimap world.
        /// </summary>
        public XRWorldInstance? WorldInstanceOverride { get; set; } = null;

        /// <summary>
        /// The resolved world instance that will be rendered to this viewport.
        /// Returns WorldInstanceOverride if set, otherwise falls back to the world containing the CameraComponent.
        /// Returns null if neither source provides a valid world (rendering will be skipped).
        /// </summary>
        public XRWorldInstance? World => WorldInstanceOverride ?? CameraComponent?.SceneNode?.World;

        /// <summary>
        /// The screen-space bounding rectangle of this viewport in pixels.
        /// Defines where on the window this viewport renders. Origin is at bottom-left.
        /// Use Resize() to properly update this and trigger related recalculations.
        /// </summary>
        public BoundingRectangle Region => _region;

        /// <summary>
        /// The internal rendering resolution bounding rectangle.
        /// May be smaller than Region for performance (render at lower res, then upscale).
        /// Render targets and framebuffers use these dimensions.
        /// </summary>
        public BoundingRectangle InternalResolutionRegion => _internalResolutionRegion;

        /// <summary>
        /// The X coordinate (in pixels) of the viewport's left edge within the window.
        /// Measured from the left edge of the window. Direct access; prefer Resize() for proper updates.
        /// </summary>
        public int X
        {
            get => _region.X;
            set => _region.X = value;
        }

        /// <summary>
        /// The Y coordinate (in pixels) of the viewport's bottom edge within the window.
        /// Measured from the bottom edge of the window. Direct access; prefer Resize() for proper updates.
        /// </summary>
        public int Y
        {
            get => _region.Y;
            set => _region.Y = value;
        }

        /// <summary>
        /// The width (in pixels) of the viewport's display area.
        /// Direct access; prefer Resize() for proper updates that trigger aspect ratio and UI recalculations.
        /// </summary>
        public int Width
        {
            get => _region.Width;
            set => _region.Width = value;
        }

        /// <summary>
        /// The height (in pixels) of the viewport's display area.
        /// Direct access; prefer Resize() for proper updates that trigger aspect ratio and UI recalculations.
        /// </summary>
        public int Height
        {
            get => _region.Height;
            set => _region.Height = value;
        }

        /// <summary>
        /// The width (in pixels) of the internal render resolution.
        /// Render targets are created at this size. May differ from Width for resolution scaling.
        /// </summary>
        public int InternalWidth => _internalResolutionRegion.Width;

        /// <summary>
        /// The height (in pixels) of the internal render resolution.
        /// Render targets are created at this size. May differ from Height for resolution scaling.
        /// </summary>
        public int InternalHeight => _internalResolutionRegion.Height;

        /// <summary>
        /// The rendering order index for this viewport within the parent window.
        /// Viewports with lower indices are rendered first (back-to-front ordering).
        /// Higher-indexed viewports render on top, useful for:
        /// - Split-screen layouts (viewports at different screen positions)
        /// - Picture-in-picture overlays (small viewport over larger one)
        /// - Debug/tool viewports rendered over the main view
        /// </summary>
        public int Index
        {
            get => _index;
            set => SetField(ref _index, value);
        }

        /// <summary>
        /// The local player controller associated with this viewport.
        /// Used for split-screen multiplayer where each player has their own viewport.
        /// Setting this property maintains a bidirectional link (player.Viewport ↔ viewport.AssociatedPlayer).
        /// Also enables player-specific features like per-player light culling and input routing.
        /// Not serialized (YamlIgnore) as player associations are established at runtime.
        /// </summary>
        [YamlIgnore]
        public LocalPlayerController? AssociatedPlayer
        {
            get => _associatedPlayer;
            internal set
            {
                if (_associatedPlayer == value)
                    return;

                _associatedPlayer?.Viewport = null;
                SetField(ref _associatedPlayer, value);
                _associatedPlayer?.Viewport = this;
            }
        }

        /// <summary>
        /// The camera component from the scene hierarchy used for rendering.
        /// Provides both the camera parameters and world-space transform from its scene node.
        /// When set, the component's Camera is automatically assigned to the Camera property.
        /// The camera component also provides UI overlay canvas and culling camera override features.
        /// Prefer this over setting Camera directly when the camera exists in the scene graph.
        /// </summary>
        public CameraComponent? CameraComponent
        {
            get => _cameraComponent;
            set => SetField(ref _cameraComponent, value);
        }

        /// <summary>
        /// The standalone camera instance used for rendering.
        /// This is automatically set when CameraComponent changes, but can also be set directly
        /// for scenarios where no CameraComponent exists (e.g., preview rendering, thumbnails).
        /// Setting this registers the viewport in the camera's Viewports list and sets up
        /// automatic visibility collection and buffer swapping if those options are enabled.
        /// </summary>
        public XRCamera? Camera
        {
            get => _camera;
            set => SetField(ref _camera, value);
        }

        /// <summary>
        /// The resolved camera instance that will be used for rendering.
        /// Returns CameraComponent.Camera if a camera component is set, otherwise falls back to Camera.
        /// This is the actual camera used in CollectVisible(), Render(), and other rendering operations.
        /// Returns null if neither source provides a camera (rendering will be skipped).
        /// </summary>
        public XRCamera? ActiveCamera => _cameraComponent?.Camera ?? _camera;

        /// <summary>
        /// The render pipeline instance that manages the complete rendering process for this viewport.
        /// Contains the mesh render command collection, framebuffer cache, and pipeline state.
        /// Not serialized (YamlIgnore) as this is runtime state that's reconstructed on load.
        /// Access this to manually control render commands or inspect rendering state.
        /// </summary>
        [YamlIgnore]
        public XRRenderPipelineInstance RenderPipelineInstance => _renderPipeline;

        /// <summary>
        /// The render pipeline definition that controls how rendering is performed.
        /// Defines the sequence of render passes (G-buffer, lighting, post-processing, etc.).
        /// Setting this directly is an alternative to using SetRenderPipelineFromCamera.
        /// Not serialized (YamlIgnore) as pipelines are typically assigned from camera settings.
        /// </summary>
        [YamlIgnore]
        public RenderPipeline? RenderPipeline
        {
            get => _renderPipeline.Pipeline;
            set => _renderPipeline.Pipeline = value;
        }

        /// <summary>
        /// When true, the RenderPipeline is automatically synchronized with the Camera's RenderPipeline property.
        /// This ensures the viewport uses the correct pipeline when the camera changes.
        /// Set to false if you need a custom pipeline independent of the camera's settings,
        /// such as a simplified pipeline for shadow map rendering or a special effects pipeline.
        /// </summary>
        public bool SetRenderPipelineFromCamera
        {
            get => _setRenderPipelineFromCamera;
            set => SetField(ref _setRenderPipelineFromCamera, value);
        }

        /// <summary>
        /// When true, visible objects are automatically collected each frame via Engine.Time.Timer.CollectVisible.
        /// This populates the render command collection with all objects visible to this viewport's camera.
        /// Set to false for manual control, such as:
        /// - Custom visibility determination algorithms
        /// - Deferred visibility collection for multi-camera setups
        /// - Rendering pre-determined object lists without culling
        /// </summary>
        public bool AutomaticallyCollectVisible
        {
            get => _automaticallyCollectVisible;
            set => SetField(ref _automaticallyCollectVisible, value);
        }

        /// <summary>
        /// When true, render command buffers are automatically double-buffered each frame via Engine.Time.Timer.SwapBuffers.
        /// Double-buffering prevents rendering artifacts from concurrent updates to render commands.
        /// Set to false for manual control, such as:
        /// - Multi-pass rendering where buffers should persist across passes
        /// - Custom synchronization with compute or async operations
        /// - Debugging render command submission
        /// </summary>
        public bool AutomaticallySwapBuffers
        {
            get => _automaticallySwapBuffers;
            set => SetField(ref _automaticallySwapBuffers, value);
        }

        /// <summary>
        /// When true, screen-space UI (HUD, menus, overlays) is rendered after the 3D scene.
        /// The UI comes from the CameraComponent's UserInterfaceOverlay canvas.
        /// Set to false to disable UI rendering for this viewport, useful for:
        /// - Reflection/refraction cameras that shouldn't show UI
        /// - Shadow map or depth-only rendering
        /// - Scene capture for thumbnails or previews
        /// </summary>
        public bool AllowUIRender
        {
            get => _allowUIRender;
            set => SetField(ref _allowUIRender, value);
        }

        /// <summary>
        /// When true, objects outside the camera's view frustum are culled (excluded from rendering).
        /// This is a major performance optimization for typical rendering scenarios.
        /// Set to false to disable frustum culling, useful for:
        /// - Shadow map rendering (needs objects outside main view)
        /// - Omnidirectional/cubemap cameras
        /// - Debugging to verify culling isn't incorrectly hiding objects
        /// Can be overridden per-call via CameraComponent.CullWithFrustum.
        /// </summary>
        public bool CullWithFrustum
        {
            get => _cullWithFrustum;
            set => SetField(ref _cullWithFrustum, value);
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new viewport attached to the specified window, covering the full window area.
        /// The viewport will automatically resize when the window resizes.
        /// </summary>
        /// <param name="window">The parent window that will contain this viewport. Can be null for off-screen rendering.</param>
        public XRViewport(XRWindow? window)
        {
            Window = window;
            Index = 0;
            SetFullScreen();
        }

        /// <summary>
        /// Creates a new viewport with explicit position and dimensions within the parent window.
        /// Use this constructor for split-screen layouts or picture-in-picture viewports.
        /// </summary>
        /// <param name="window">The parent window that will contain this viewport. Can be null for off-screen rendering.</param>
        /// <param name="x">The X coordinate (pixels) of the viewport's left edge within the window.</param>
        /// <param name="y">The Y coordinate (pixels) of the viewport's bottom edge within the window.</param>
        /// <param name="width">The width (pixels) of the viewport.</param>
        /// <param name="height">The height (pixels) of the viewport.</param>
        /// <param name="index">The rendering order index. Lower indices render first (default: 0).</param>
        public XRViewport(XRWindow? window, int x, int y, uint width, uint height, int index = 0)
        {
            Window = window;
            X = x;
            Y = y;
            Index = index;
            Resize(width, height);
        }

        /// <summary>
        /// Creates a new full-screen viewport with specified dimensions.
        /// The viewport covers the entire window but uses the specified dimensions for internal resolution.
        /// </summary>
        /// <param name="window">The parent window that will contain this viewport. Can be null for off-screen rendering.</param>
        /// <param name="width">The width (pixels) to use for the viewport and internal resolution.</param>
        /// <param name="height">The height (pixels) to use for the viewport and internal resolution.</param>
        public XRViewport(XRWindow? window, uint width, uint height)
        {
            Window = window;
            Index = 0;
            SetFullScreen();
            Resize(width, height);
        }

        #endregion

        #region Static Factory Methods

        /// <summary>
        /// Factory method that creates a viewport configured for split-screen layouts based on total player count.
        /// Automatically positions the viewport according to the current viewport count and player layout preferences.
        /// For example, with 2 players it creates either left/right or top/bottom splits depending on TwoPlayerViewportPreference.
        /// </summary>
        /// <param name="window">The parent window that will contain this viewport.</param>
        /// <param name="totalViewportCount">The current number of existing viewports (determines this viewport's index and position).</param>
        /// <returns>A new viewport configured for the appropriate split-screen position.</returns>
        public static XRViewport ForTotalViewportCount(XRWindow window, int totalViewportCount)
        {
            int index = totalViewportCount;
            XRViewport viewport = new(window);
            if (index == 0)
            {
                viewport.Index = index;
                viewport.SetFullScreen();
            }
            else
                viewport.ViewportCountChanged(
                    index,
                    totalViewportCount + 1,
                    Engine.GameSettings.TwoPlayerViewportPreference,
                    Engine.GameSettings.ThreePlayerViewportPreference);

            viewport.Index = index;
            return viewport;
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// Cleans up the viewport by releasing camera references.
        /// This unregisters the viewport from the camera's Viewports list and removes event subscriptions.
        /// Call this when disposing of a viewport to prevent memory leaks and dangling references.
        /// </summary>
        public void Destroy()
        {
            Camera = null;
            CameraComponent = null;
        }

        /// <summary>
        /// Ensures this viewport is registered in the active camera's Viewports list.
        /// Call this after play mode transitions or snapshot restore to fix broken bindings.
        /// </summary>
        public void EnsureViewportBoundToCamera()
        {
            var camera = ActiveCamera;
            Debug.Rendering($"[XRViewport] EnsureViewportBoundToCamera: VP[{Index}] ActiveCamera={camera?.GetHashCode().ToString() ?? "NULL"} _camera={_camera?.GetHashCode().ToString() ?? "NULL"} _cameraComponent?.Camera={_cameraComponent?.Camera?.GetHashCode().ToString() ?? "NULL"}");
            if (camera is null)
                return;

            if (!camera.Viewports.Contains(this))
            {
                Debug.Rendering($"[XRViewport] EnsureViewportBoundToCamera: Adding VP[{Index}] to camera {camera.GetHashCode()} Viewports (was missing, now count={camera.Viewports.Count + 1})");
                camera.Viewports.Add(this);
            }
            else
            {
                Debug.Rendering($"[XRViewport] EnsureViewportBoundToCamera: VP[{Index}] already in camera {camera.GetHashCode()} Viewports (count={camera.Viewports.Count})");
            }
        }

        #endregion

        #region Property Change Handlers

        /// <summary>
        /// Called before a property value changes. Handles cleanup of old values.
        /// Specifically manages unsubscription from engine timer events when automatic
        /// visibility collection or buffer swapping settings change, and removes this
        /// viewport from the old camera's Viewports list when the camera changes.
        /// </summary>
        /// <typeparam name="T">The type of the property being changed.</typeparam>
        /// <param name="propName">The name of the property being changed.</param>
        /// <param name="field">The current (old) value of the property.</param>
        /// <param name="new">The new value being assigned to the property.</param>
        /// <returns>True if the change should proceed, false to cancel the change.</returns>
        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(AutomaticallySwapBuffers):
                        if (_automaticallySwapBuffers && _camera is not null)
                            Engine.Time.Timer.SwapBuffers -= SwapBuffersAutomatic;
                        break;
                    case nameof(AutomaticallyCollectVisible):
                        if (_automaticallyCollectVisible && _camera is not null)
                            Engine.Time.Timer.CollectVisible -= CollectVisibleAutomatic;
                        break;
                    case nameof(Camera):
                        if (_camera is not null)
                        {
                            _camera.Viewports.Remove(this);

                            if (AutomaticallySwapBuffers)
                                Engine.Time.Timer.SwapBuffers -= SwapBuffersAutomatic;

                            if (AutomaticallyCollectVisible)
                                Engine.Time.Timer.CollectVisible -= CollectVisibleAutomatic;
                        }
                        break;
                }
            }
            return change;
        }

        /// <summary>
        /// Called after a property value has changed. Handles setup for new values.
        /// Specifically manages subscription to engine timer events when automatic
        /// visibility collection or buffer swapping settings change, registers this
        /// viewport in the new camera's Viewports list, syncs the render pipeline,
        /// and resizes UI elements when the camera component changes.
        /// </summary>
        /// <typeparam name="T">The type of the property that changed.</typeparam>
        /// <param name="propName">The name of the property that changed.</param>
        /// <param name="prev">The previous value of the property.</param>
        /// <param name="field">The new (current) value of the property.</param>
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(AutomaticallySwapBuffers):
                    if (_automaticallySwapBuffers && _camera is not null)
                        Engine.Time.Timer.SwapBuffers += SwapBuffersAutomatic;
                    break;
                case nameof(AutomaticallyCollectVisible):
                    if (_automaticallyCollectVisible && _camera is not null)
                        Engine.Time.Timer.CollectVisible += CollectVisibleAutomatic;
                    break;
                case nameof(Camera):
                    if (_camera is not null)
                    {
                        if (!_camera.Viewports.Contains(this))
                            _camera.Viewports.Add(this);
                        SetAspectRatioToCamera();

                        if (AutomaticallySwapBuffers)
                            Engine.Time.Timer.SwapBuffers += SwapBuffersAutomatic;

                        if (AutomaticallyCollectVisible)
                            Engine.Time.Timer.CollectVisible += CollectVisibleAutomatic;
                    }
                    if (SetRenderPipelineFromCamera)
                    {
                        _renderPipeline.Pipeline = _camera?.RenderPipeline;

                        // When the camera (and therefore pipeline) changes, we must push current
                        // sizing into the new pipeline instance. Otherwise the new pipeline can run
                        // with stale dimensions until a window resize occurs.
                        _renderPipeline.InternalResolutionResized(InternalWidth, InternalHeight);
                        _renderPipeline.ViewportResized(Width, Height);

                        // When the camera changes, destroy the pipeline cache to ensure
                        // stale textures/FBOs from the previous camera don't persist.
                        _renderPipeline.DestroyCache();
                    }
                    break;
                case nameof(CameraComponent):
                    ResizeCameraComponentUI();
                    // Set the Camera property - this will handle viewport binding if the reference changes
                    var newCam = CameraComponent?.Camera;
                    Debug.Rendering($"[XRViewport] CameraComponent changed: VP[{Index}] OldCamera={_camera?.GetHashCode().ToString() ?? "NULL"} NewCamera={newCam?.GetHashCode().ToString() ?? "NULL"} CamCompName={CameraComponent?.Name ?? "<null>"} CamCompHash={CameraComponent?.GetHashCode().ToString() ?? "null"}");
                    Camera = newCam;
                    // IMPORTANT: Even if Camera reference didn't change, we must ensure this viewport
                    // is in the camera's Viewports list. This can happen when:
                    // 1. The editor pawn survives snapshot restore (same objects reused)
                    // 2. The viewport was removed from camera.Viewports during play mode
                    // 3. SetField didn't detect a change because references are equal
                    EnsureViewportBoundToCamera();
                    Debug.Rendering($"[XRViewport] After EnsureViewportBoundToCamera: VP[{Index}] Camera.Viewports.Count={ActiveCamera?.Viewports.Count ?? -1}");
                    //_renderPipeline.Pipeline = CameraComponent?.RenderPipeline;
                    break;
            }
        }

        #endregion

        #region Visibility Collection

        /// <summary>
        /// Collects all visible items in the world and UI for rendering.
        /// This method determines which objects are visible to the camera and adds them to the render command collection.
        /// It performs frustum culling (if enabled), updates light intersections for player viewports,
        /// and collects screen-space UI elements.
        /// 
        /// This is typically called automatically each frame when AutomaticallyCollectVisible is true,
        /// but can be called manually for custom rendering scenarios.
        /// </summary>
        /// <param name="collectMirrors">When true, also collects mirror/reflection surfaces for recursive rendering. Default: true.</param>
        /// <param name="worldOverride">Optional world instance to collect from instead of the viewport's World property.</param>
        /// <param name="cameraOverride">Optional camera to use for visibility determination instead of ActiveCamera.</param>
        /// <param name="renderCommandsOverride">Optional render command collection to populate instead of the pipeline's default collection.</param>
        /// <param name="allowScreenSpaceUICollectVisible">When true, also collects screen-space UI elements. Default: true.</param>
        /// <param name="collectionVolumeOverride">Optional custom volume for visibility testing instead of the camera's frustum.</param>
        public void CollectVisible(
            bool collectMirrors = true,
            XRWorldInstance? worldOverride = null,
            XRCamera? cameraOverride = null,
            RenderCommandCollection? renderCommandsOverride = null,
            bool allowScreenSpaceUICollectVisible = true,
            IVolume? collectionVolumeOverride = null)
        {
            using var sample = Engine.Profiler.Start("XRViewport.CollectVisible");
            
/*
            Debug.RenderingEvery(
                $"XRViewport.CollectVisible.Tick.{GetHashCode()}[{Index}].{Engine.PlayMode.State}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] CollectVisible tick. PlayMode={0} VP[{1}] AutoCollect={2} CameraNull={3} WorldNull={4} PipelineNull={5} AssocPlayer={6}",
                Engine.PlayMode.State,
                Index,
                AutomaticallyCollectVisible,
                ActiveCamera is null,
                (worldOverride ?? World) is null,
                _renderPipeline.Pipeline is null,
                AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>");
*/

            XRCamera? camera = cameraOverride ?? ActiveCamera;
            if (camera is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.CollectVisible.NoCamera.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] CollectVisible skipped: no ActiveCamera. VP[{0}] AssocPlayer={1} CameraComponentNull={2} CameraFieldNull={3}",
                    Index,
                    AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>",
                    CameraComponent is null,
                    Camera is null);
                return;
            }

            XRWorldInstance? world = worldOverride ?? World;

            if (world is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.CollectVisible.NoWorld.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] CollectVisible skipped: no World. VP[{0}] WorldOverrideNull={1} WorldInstanceOverrideNull={2} CameraNodeWorldNull={3}",
                    Index,
                    worldOverride is null,
                    WorldInstanceOverride is null,
                    CameraComponent?.SceneNode?.World is null);
                return;
            }

            if (world.VisualScene is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.CollectVisible.NoVisualScene.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] CollectVisible skipped: world.VisualScene is null. VP[{0}] World={1}",
                    Index,
                    world.TargetWorld?.Name ?? "<unknown>");
                return;
            }

            // Only run player-view light intersection bookkeeping for player-associated viewports.
            if (AssociatedPlayer is not null && world is not null)
                world.Lights.UpdateCameraLightIntersections(camera);

            var commandCollection = renderCommandsOverride ?? _renderPipeline.MeshRenderCommands;
            int beforeUpdatingCount = 0;
            //if (Environment.GetEnvironmentVariable("XRE_DEBUG_RENDER_SUBMIT") == "1")
                beforeUpdatingCount = commandCollection.GetUpdatingCommandCount();

            world?.VisualScene?.CollectRenderedItems(
                commandCollection,
                camera,
                CameraComponent?.CullWithFrustum ?? CullWithFrustum,
                CameraComponent?.CullingCameraOverride,
                collectionVolumeOverride,
                collectMirrors);

            //if (Environment.GetEnvironmentVariable("XRE_DEBUG_RENDER_SUBMIT") == "1")
            {
                int afterUpdatingCount = commandCollection.GetUpdatingCommandCount();
                int delta = afterUpdatingCount - beforeUpdatingCount;

                string visualSceneType = world?.VisualScene?.GetType().Name ?? "<null>";
                int trackedRenderables = world?.VisualScene?.Renderables?.Count ?? -1;

                (uint Draws, uint Instances) gpuVisible = (0, 0);
                if (world?.VisualScene is VisualScene3D vs3d)
                    gpuVisible = vs3d.LastGpuVisibility;

/*
                Debug.RenderingEvery(
                    $"XRViewport.CollectVisible.Submit.{GetHashCode()}[{Index}].{Engine.PlayMode.State}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] CollectVisible submit. PlayMode={0} VP[{1}] CmdsUpdating={2} Delta={3} VisualScene={4} TrackedRenderables={5} GpuVisibleDraws={6} GpuVisibleInstances={7}",
                    Engine.PlayMode.State,
                    Index,
                    afterUpdatingCount,
                    delta,
                    visualSceneType,
                    trackedRenderables,
                    gpuVisible.Draws,
                    gpuVisible.Instances);
*/
            }

            if (allowScreenSpaceUICollectVisible)
                CollectVisible_ScreenSpaceUI();
        }

        /// <summary>
        /// Collects screen space UI items into the canvas' render pipeline.
        /// If AllowUIRender is false, the camera component has no UI canvas, or the canvas is not set to screen space, this will do nothing.
        /// </summary>
        private void CollectVisible_ScreenSpaceUI()
        {
            using var sample = Engine.Profiler.Start("XRViewport.CollectVisible_ScreenSpaceUI");

            if (!AllowUIRender)
                return;

            UICanvasComponent? ui = CameraComponent?.GetUserInterfaceOverlay();
            if (ui is null)
                return;

            if (ui.CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
                ui?.CollectVisibleItemsScreenSpace();
        }

        /// <summary>
        /// Internal callback for automatic visibility collection.
        /// Subscribed to Engine.Time.Timer.CollectVisible when AutomaticallyCollectVisible is true.
        /// Simply delegates to CollectVisible() with default parameters.
        /// </summary>
        private void CollectVisibleAutomatic() => CollectVisible();

        #endregion

        #region Buffer Swapping

        /// <summary>
        /// Swaps the double-buffered render command collections.
        /// This transfers commands collected during visibility collection to the rendering buffer,
        /// allowing new commands to be collected while the previous frame renders.
        /// 
        /// Double-buffering prevents race conditions between the update thread (collecting visible objects)
        /// and the render thread (drawing objects). Call this after CollectVisible() and before Render().
        /// 
        /// This is typically called automatically each frame when AutomaticallySwapBuffers is true,
        /// but can be called manually for custom rendering pipelines.
        /// </summary>
        /// <param name="renderCommandsOverride">Optional render command collection to swap instead of the pipeline's default collection.</param>
        /// <param name="allowScreenSpaceUISwap">When true, also swaps buffers for screen-space UI. Default: true.</param>
        public void SwapBuffers(
            RenderCommandCollection? renderCommandsOverride = null,
            bool allowScreenSpaceUISwap = true)
        {
            using var sample = Engine.Profiler.Start($"XRViewport.SwapBuffers[{Index}]");

/*
            Debug.RenderingEvery(
                $"XRViewport.SwapBuffers.Tick.{GetHashCode()}[{Index}].{Engine.PlayMode.State}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] SwapBuffers tick. PlayMode={0} VP[{1}] AutoSwap={2} AssocPlayer={3}",
                Engine.PlayMode.State,
                Index,
                AutomaticallySwapBuffers,
                AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>");
*/
            var commandCollection = renderCommandsOverride ?? _renderPipeline.MeshRenderCommands;
            using (Engine.Profiler.Start("XRViewport.SwapBuffers.MeshCommands"))
            {
                commandCollection.SwapBuffers();
            }

            if (allowScreenSpaceUISwap)
            {
                using var uiSample = Engine.Profiler.Start("XRViewport.SwapBuffers.ScreenSpaceUI");
                SwapBuffers_ScreenSpaceUI();
            }
        }

        /// <summary>
        /// Swaps the screen space UI buffers.
        /// If AllowUIRender is false, the camera component has no UI canvas, or the canvas is not set to screen space, this will do nothing.
        /// </summary>
        private void SwapBuffers_ScreenSpaceUI()
        {
            if (!AllowUIRender)
                return;

            using var sample = Engine.Profiler.Start("XRViewport.SwapBuffers_ScreenSpaceUI");

            var ui = CameraComponent?.GetUserInterfaceOverlay();
            if (ui is null)
                return;

            if (ui.CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
                ui?.SwapBuffersScreenSpace();
        }

        /// <summary>
        /// Internal callback for automatic buffer swapping.
        /// Subscribed to Engine.Time.Timer.SwapBuffers when AutomaticallySwapBuffers is true.
        /// Delegates to SwapBuffers() with default parameters, wrapped in a profiler sample.
        /// </summary>
        private void SwapBuffersAutomatic()
        {
            using var sample = Engine.Profiler.Start("XRViewport.SwapBuffersAutomatic");
            SwapBuffers();
        }

        #endregion

        #region Rendering

        /// <summary>
        /// Renders the scene from this viewport's camera to the specified framebuffer.
        /// This is the main rendering entry point that executes the full render pipeline,
        /// including geometry passes, lighting, post-processing, and screen-space UI overlay.
        /// 
        /// The method performs several validation checks before rendering:
        /// - Ensures a camera is available (ActiveCamera or cameraOverride)
        /// - Ensures a world is available (World or worldOverride)
        /// - Checks for render recursion (same viewport already rendering)
        /// - Logs warnings for missing pipeline configurations
        /// 
        /// Before calling Render(), ensure CollectVisible() and SwapBuffers() have been called
        /// (or that automatic collection/swapping is enabled).
        /// </summary>
        /// <param name="targetFbo">The framebuffer to render to. If null, renders to the default framebuffer (screen).</param>
        /// <param name="worldOverride">Optional world instance to render instead of the viewport's World property.</param>
        /// <param name="cameraOverride">Optional camera to render from instead of ActiveCamera.</param>
        /// <param name="shadowPass">When true, renders a shadow map pass with depth-only output. Default: false.</param>
        /// <param name="forcedMaterial">When set, all objects render with this material instead of their assigned materials. Useful for debug visualization.</param>
        public void Render(
            XRFrameBuffer? targetFbo = null,
            XRWorldInstance? worldOverride = null,
            XRCamera? cameraOverride = null,
            bool shadowPass = false,
            XRMaterial? forcedMaterial = null)
        {
            using var sample = Engine.Profiler.Start("XRViewport.Render");

            XRCamera? camera = cameraOverride ?? ActiveCamera;
            if (camera is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.Render.NoCamera.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Render skipped: no ActiveCamera. VP[{0}] AssocPlayer={1} CameraComponentNull={2} CameraFieldNull={3}",
                    Index,
                    AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>",
                    CameraComponent is null,
                    Camera is null);
                return;
            }

            var world = worldOverride ?? World;
            if (world is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.Render.NoWorld.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Render skipped: no World. VP[{0}] WorldOverrideNull={1} WorldInstanceOverrideNull={2} CameraNodeWorldNull={3}",
                    Index,
                    worldOverride is null,
                    WorldInstanceOverride is null,
                    CameraComponent?.SceneNode?.World is null);
                return;
            }

            if (world.VisualScene is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.Render.NoVisualScene.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Render skipped: world.VisualScene is null. VP[{0}] World={1}",
                    Index,
                    world.TargetWorld?.Name ?? "<unknown>");
                return;
            }

            if (_renderPipeline.Pipeline is null)
            {
                Debug.RenderingWarningEvery(
                    $"XRViewport.Render.NoPipeline.{GetHashCode()}[{Index}]",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Render running with null pipeline. VP[{0}] CameraComponent={1} CameraRenderPipelineNull={2}",
                    Index,
                    CameraComponent?.GetType().Name ?? "<null>",
                    CameraComponent?.Camera.RenderPipeline is null);
            }

            if (State.RenderingPipelineState?.ViewportStack.Contains(this) ?? false)
            {
                Debug.Rendering("Render recursion: Viewport is already currently rendering.");
                return;
            }

            // Diagnostic: Log camera transform state during render to help diagnose play mode transition issues
            /*
            Debug.RenderingEvery(
                $"XRViewport.Render.CameraState.{GetHashCode()}[{Index}]",
                TimeSpan.FromSeconds(2),
                "[RenderDiag] VP[{0}] Render CameraState: CamHash={1} CamCompHash={2} CamCompName={3} TfmHash={4} TfmPos={5:F2},{6:F2},{7:F2} RenderPos={8:F2},{9:F2},{10:F2} PlayMode={11}",
                Index,
                camera.GetHashCode(),
                CameraComponent?.GetHashCode().ToString() ?? "null",
                CameraComponent?.Name ?? "<null>",
                camera.Transform?.GetHashCode().ToString() ?? "null",
                camera.Transform?.WorldTranslation.X ?? 0,
                camera.Transform?.WorldTranslation.Y ?? 0,
                camera.Transform?.WorldTranslation.Z ?? 0,
                camera.Transform?.RenderTranslation.X ?? 0,
                camera.Transform?.RenderTranslation.Y ?? 0,
                camera.Transform?.RenderTranslation.Z ?? 0,
                Engine.PlayMode.State);
            */
            
            //using (Engine.Profiler.Start("XRViewport.Render"))
            {
                // Visibility-driven compute deformation (skinning/blendshapes).
                // This runs on the render thread and uses the swapped (rendering) command buffers.
                SkinningPrepassDispatcher.Instance.RunVisible(_renderPipeline.MeshRenderCommands);

                _renderPipeline.Render(
                    world.VisualScene,
                    camera,
                    null,
                    this,
                    targetFbo,
                    null,
                    shadowPass,
                    false,
                    forcedMaterial);

                RenderScreenSpaceUIOverlay(targetFbo);
            }
        }

        /// <summary>
        /// Renders the scene in stereoscopic 3D mode for VR headsets or 3D displays.
        /// Renders both left and right eye views in a single pass using the provided cameras.
        /// The stereo rendering mode is handled by the render pipeline, which may use techniques
        /// like single-pass instanced stereo or multi-view rendering for efficiency.
        /// 
        /// After stereo rendering completes, the screen-space UI overlay is rendered on top.
        /// </summary>
        /// <param name="targetFbo">The framebuffer to render to (typically a VR headset's texture). May be null for default framebuffer.</param>
        /// <param name="leftCamera">The camera representing the left eye viewpoint. Includes appropriate eye offset and projection.</param>
        /// <param name="rightCamera">The camera representing the right eye viewpoint. Includes appropriate eye offset and projection.</param>
        /// <param name="worldOverride">Optional world instance to render instead of the viewport's World property.</param>
        public void RenderStereo(
            XRFrameBuffer? targetFbo,
            XRCamera? leftCamera,
            XRCamera? rightCamera,
            XRWorldInstance? worldOverride = null)
        {
            var world = worldOverride ?? World;
            if (world is null)
            {
                Debug.Rendering("No world is set to this viewport.");
                return;
            }

            if (State.RenderingPipelineState?.ViewportStack.Contains(this) ?? false)
            {
                Debug.Rendering("Render recursion: Viewport is already currently rendering.");
                return;
            }

            _renderPipeline.Render(
                world.VisualScene,
                leftCamera,
                rightCamera,
                this,
                targetFbo,
                null,
                false,
                true,
                null);

            RenderScreenSpaceUIOverlay(targetFbo);
        }

        /// <summary>
        /// Renders the screen-space UI overlay on top of the 3D scene.
        /// This is called at the end of Render() and RenderStereo() to composite UI elements.
        /// 
        /// The method checks several conditions before rendering:
        /// - AllowUIRender must be true
        /// - CameraComponent must have a UI overlay canvas
        /// - The canvas must be active and set to screen-space draw mode
        /// 
        /// The UI is rendered using its own pipeline, preserving the existing framebuffer contents.
        /// </summary>
        /// <param name="targetFbo">The framebuffer to render the UI to (same as the main render target).</param>
        private void RenderScreenSpaceUIOverlay(XRFrameBuffer? targetFbo)
        {
            if (!AllowUIRender)
                return;

            var ui = CameraComponent?.GetUserInterfaceOverlay();
            if (ui is null || !ui.IsActive)
                return;

            if (ui.CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen)
                return;

            // Render the UI using its own pipeline on top of whatever the camera pipeline produced.
            ui.RenderScreenSpace(this, targetFbo);
        }

        #endregion

        #region Resizing

        /// <summary>
        /// Resizes the viewport based on the parent window dimensions and the viewport's percentage settings.
        /// This is the primary method for updating viewport dimensions and should be called when:
        /// - The parent window is resized
        /// - The viewport's percentage layout changes
        /// - Initial viewport setup
        /// 
        /// The method performs several updates:
        /// 1. Calculates pixel dimensions from window size and percentage settings
        /// 2. Optionally updates internal rendering resolution
        /// 3. Resizes the camera component's UI overlay
        /// 4. Updates the camera's aspect ratio (if inheriting from viewport)
        /// 5. Notifies the render pipeline of the size change
        /// 6. Raises the Resized event
        /// </summary>
        /// <param name="windowWidth">The current width of the parent window in pixels.</param>
        /// <param name="windowHeight">The current height of the parent window in pixels.</param>
        /// <param name="setInternalResolution">When true, also updates internal resolution to match (or use override values). Default: true.</param>
        /// <param name="internalResolutionWidth">Override for internal width. If <= 0, uses calculated viewport width. Default: -1.</param>
        /// <param name="internalResolutionHeight">Override for internal height. If <= 0, uses calculated viewport height. Default: -1.</param>
        public void Resize(
            uint windowWidth,
            uint windowHeight,
            bool setInternalResolution = true,
            int internalResolutionWidth = -1,
            int internalResolutionHeight = -1)
        {
            float w = windowWidth.ClampMin(1u);
            float h = windowHeight.ClampMin(1u);

            _region.X = (int)(_leftPercentage * w);
            _region.Y = (int)(_bottomPercentage * h);
            _region.Width = (int)(_rightPercentage * w - _region.X);
            _region.Height = (int)(_topPercentage * h - _region.Y);

            if (setInternalResolution)
                SetInternalResolution(
                    internalResolutionWidth <= 0 ? _region.Width : internalResolutionWidth,
                    internalResolutionHeight <= 0 ? _region.Height : internalResolutionHeight,
                    true);

            ResizeCameraComponentUI();
            SetAspectRatioToCamera();
            ResizeRenderPipeline();
            Resized?.Invoke(this);
        }

        /// <summary>
        /// Sets the internal rendering resolution, which may differ from the display resolution.
        /// Use this for resolution scaling, where the scene is rendered at a lower resolution
        /// and upscaled to the display resolution for improved performance.
        /// 
        /// When correctAspect is true, the internal resolution is adjusted to maintain
        /// the same aspect ratio as the viewport's display region, preventing distortion.
        /// 
        /// After setting, the render pipeline is notified and the InternalResolutionResized event is raised.
        /// </summary>
        /// <param name="width">The desired internal rendering width in pixels.</param>
        /// <param name="height">The desired internal rendering height in pixels.</param>
        /// <param name="correctAspect">When true, adjusts dimensions to match the viewport's aspect ratio.</param>
        public void SetInternalResolution(int width, int height, bool correctAspect)
        {
            _internalResolutionRegion.Width = width;
            _internalResolutionRegion.Height = height;
            if (correctAspect)
            {
                //Shrink the internal resolution to fit the aspect ratio of the viewport
                float aspect = (float)_region.Width / _region.Height;
                if (aspect > 1.0f)
                    _internalResolutionRegion.Height = (int)(_internalResolutionRegion.Width / aspect);
                else
                    _internalResolutionRegion.Width = (int)(_internalResolutionRegion.Height * aspect);
            }
            _renderPipeline.InternalResolutionResized(InternalWidth, InternalHeight);
            InternalResolutionResized?.Invoke(this);
        }

        /// <summary>
        /// Sets the internal rendering resolution as a percentage of the viewport's display resolution.
        /// Useful for dynamic resolution scaling based on performance metrics.
        /// For example, SetInternalResolutionPercentage(0.5f, 0.5f) renders at half resolution.
        /// </summary>
        /// <param name="widthPercent">The internal width as a fraction (0.0-1.0) of display width.</param>
        /// <param name="heightPercent">The internal height as a fraction (0.0-1.0) of display height.</param>
        public void SetInternalResolutionPercentage(float widthPercent, float heightPercent)
            => SetInternalResolution((int)(widthPercent * _region.Width), (int)(heightPercent * _region.Height), true);

        /// <summary>
        /// Notifies the render pipeline that the viewport display dimensions have changed.
        /// Called internally by Resize() to update framebuffer sizes and other resolution-dependent resources.
        /// </summary>
        private void ResizeRenderPipeline()
            => _renderPipeline.ViewportResized(Width, Height);

        /// <summary>
        /// Updates the camera's aspect ratio to match the viewport dimensions.
        /// Applies to perspective cameras with InheritAspectRatio enabled,
        /// and orthographic cameras with InheritAspectRatio enabled.
        /// Called internally when the viewport resizes or camera changes.
        /// </summary>
        private void SetAspectRatioToCamera()
        {
            if (ActiveCamera?.Parameters is XRPerspectiveCameraParameters p && p.InheritAspectRatio)
            {
                p.AspectRatio = (float)_region.Width / _region.Height;
            }
            else if (ActiveCamera?.Parameters is XROrthographicCameraParameters o && o.InheritAspectRatio)
            {
                o.SetAspectRatio(_region.Width, _region.Height);
            }
        }

        /// <summary>
        /// Updates the camera component's UI overlay canvas dimensions to match the viewport.
        /// Called internally when the viewport resizes or camera component changes.
        /// Ensures screen-space UI elements are correctly positioned and scaled.
        /// </summary>
        private void ResizeCameraComponentUI()
        {
            var overlay = CameraComponent?.GetUserInterfaceOverlay();
            if (overlay is null)
                return;

            // Don't set invalid (zero or negative) sizes - this can happen if
            // CameraComponent is set before the viewport is properly resized
            if (_region.Size.X <= 0 || _region.Size.Y <= 0)
            {
                Debug.Rendering($"[XRViewport] ResizeCameraComponentUI: Skipping resize due to invalid region size ({_region.Size.X}x{_region.Size.Y})");
                return;
            }

            var tfm = overlay.CanvasTransform;
            Debug.Rendering($"[XRViewport] ResizeCameraComponentUI: Setting canvas size to {_region.Size.X}x{_region.Size.Y} (was {tfm.Width}x{tfm.Height})");
            tfm.SetSize(_region.Size);
        }

        #endregion

        #region Coordinate Conversion

        /// <summary>
        /// Converts a window (screen) coordinate to a viewport-local coordinate.
        /// Subtracts the viewport's position offset so (0,0) is at the viewport's bottom-left corner.
        /// </summary>
        /// <param name="coord">The window coordinate to convert.</param>
        /// <returns>The coordinate relative to the viewport's origin.</returns>
        public Vector2 ScreenToViewportCoordinate(Vector2 coord)
            => new(coord.X - _region.X, coord.Y - _region.Y);

        /// <summary>
        /// Converts a window (screen) coordinate to a viewport-local coordinate in-place.
        /// Modifies the input vector directly for efficiency.
        /// </summary>
        /// <param name="coord">The window coordinate to convert (modified in-place).</param>
        public void ScreenToViewportCoordinate(ref Vector2 coord)
            => coord = ScreenToViewportCoordinate(coord);

        /// <summary>
        /// Converts a viewport-local coordinate to a window (screen) coordinate.
        /// Adds the viewport's position offset to get absolute window position.
        /// </summary>
        /// <param name="coord">The viewport-local coordinate to convert.</param>
        /// <returns>The coordinate in window space.</returns>
        public Vector2 ViewportToScreenCoordinate(Vector2 coord)
            => new(coord.X + _region.X, coord.Y + _region.Y);

        /// <summary>
        /// Converts a viewport-local coordinate to a window (screen) coordinate in-place.
        /// Modifies the input vector directly for efficiency.
        /// </summary>
        /// <param name="coord">The viewport-local coordinate to convert (modified in-place).</param>
        public void ViewportToScreenCoordinate(ref Vector2 coord)
            => coord = ViewportToScreenCoordinate(coord);

        /// <summary>
        /// Converts a viewport display coordinate to internal resolution coordinate.
        /// Scales the coordinate based on the ratio between internal and display resolutions.
        /// Use when mapping mouse clicks to render target pixels.
        /// </summary>
        /// <param name="viewportPoint">The point in viewport display coordinates.</param>
        /// <returns>The point in internal resolution coordinates.</returns>
        public Vector2 ViewportToInternalCoordinate(Vector2 viewportPoint)
            => viewportPoint * (InternalResolutionRegion.Size / _region.Size);

        /// <summary>
        /// Converts an internal resolution coordinate to viewport display coordinate.
        /// Scales the coordinate based on the ratio between display and internal resolutions.
        /// Use when mapping render target positions to screen positions.
        /// </summary>
        /// <param name="viewportPoint">The point in internal resolution coordinates.</param>
        /// <returns>The point in viewport display coordinates.</returns>
        public Vector2 InternalToViewportCoordinate(Vector2 viewportPoint)
            => viewportPoint * (_region.Size / InternalResolutionRegion.Size);

        /// <summary>
        /// Normalizes a viewport coordinate to the range [0,1].
        /// (0,0) represents the bottom-left corner, (1,1) the top-right.
        /// Use for camera ray generation or UV-style coordinate systems.
        /// </summary>
        /// <param name="viewportPoint">The point in pixel coordinates.</param>
        /// <returns>The normalized coordinate in [0,1] range.</returns>
        public Vector2 NormalizeViewportCoordinate(Vector2 viewportPoint)
            => viewportPoint / _region.Size;

        /// <summary>
        /// Converts a normalized [0,1] coordinate back to viewport pixel coordinates.
        /// Inverse of NormalizeViewportCoordinate.
        /// </summary>
        /// <param name="normalizedViewportPoint">The normalized coordinate to convert.</param>
        /// <returns>The point in viewport pixel coordinates.</returns>
        public Vector2 DenormalizeViewportCoordinate(Vector2 normalizedViewportPoint)
            => normalizedViewportPoint * _region.Size;

        /// <summary>
        /// Normalizes an internal resolution coordinate to the range [0,1].
        /// Similar to NormalizeViewportCoordinate but uses internal resolution dimensions.
        /// </summary>
        /// <param name="viewportPoint">The point in internal resolution pixel coordinates.</param>
        /// <returns>The normalized coordinate in [0,1] range.</returns>
        public Vector2 NormalizeInternalCoordinate(Vector2 viewportPoint)
            => viewportPoint / _internalResolutionRegion.Size;

        /// <summary>
        /// Converts a normalized [0,1] coordinate back to internal resolution pixel coordinates.
        /// Inverse of NormalizeInternalCoordinate.
        /// </summary>
        /// <param name="normalizedViewportPoint">The normalized coordinate to convert.</param>
        /// <returns>The point in internal resolution pixel coordinates.</returns>
        public Vector2 DenormalizeInternalCoordinate(Vector2 normalizedViewportPoint)
            => normalizedViewportPoint * _internalResolutionRegion.Size;

        /// <summary>
        /// Converts a normalized viewport coordinate and depth to a world-space position.
        /// Uses the camera's inverse view-projection matrix to unproject the screen point.
        /// </summary>
        /// <param name="normalizedViewportPoint">The normalized [0,1] viewport coordinate.</param>
        /// <param name="depth">The depth value (0 = near plane, 1 = far plane in typical configurations).</param>
        /// <returns>The world-space position corresponding to the screen point and depth.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no camera is set to this viewport.</exception>
        public Vector3 NormalizedViewportToWorldCoordinate(Vector2 normalizedViewportPoint, float depth)
            => _camera?.NormalizedViewportToWorldCoordinate(normalizedViewportPoint, depth)
                ?? throw new InvalidOperationException("No camera is set to this viewport.");

        /// <summary>
        /// Converts a normalized viewport coordinate with depth (as Vector3) to world-space.
        /// Convenience overload that extracts XY as screen position and Z as depth.
        /// </summary>
        /// <param name="normalizedViewportPoint">The viewport coordinate where X,Y are normalized position and Z is depth.</param>
        /// <returns>The world-space position.</returns>
        public Vector3 NormalizedViewportToWorldCoordinate(Vector3 normalizedViewportPoint)
            => NormalizedViewportToWorldCoordinate(new Vector2(normalizedViewportPoint.X, normalizedViewportPoint.Y), normalizedViewportPoint.Z);

        /// <summary>
        /// Converts a world-space position to normalized viewport coordinates.
        /// Projects the 3D point onto the screen using the camera's view-projection matrix.
        /// The returned Z component represents the depth (distance from camera).
        /// </summary>
        /// <param name="worldPoint">The world-space position to project.</param>
        /// <returns>The viewport coordinate where X,Y are normalized screen position and Z is depth.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no camera is set to this viewport.</exception>
        public Vector3 WorldToNormalizedViewportCoordinate(Vector3 worldPoint)
            => _camera?.WorldToNormalizedViewportCoordinate(worldPoint)
                ?? throw new InvalidOperationException("No camera is set to this viewport.");

        #endregion

        #region Picking

        /// <summary>
        /// Reads the depth buffer value at the specified viewport position from the default framebuffer.
        /// Uses synchronous GPU readback which may cause pipeline stalls.
        /// For performance-critical code, consider using GetDepthAsync instead.
        /// </summary>
        /// <param name="viewportPoint">The viewport-local coordinates to sample.</param>
        /// <returns>The depth buffer value (typically 0.0 = near plane, 1.0 = far plane).</returns>
        public static float GetDepth(IVector2 viewportPoint)
        {
            State.UnbindFrameBuffers(EFramebufferTarget.ReadFramebuffer);
            State.SetReadBuffer(EReadBufferMode.None);
            return State.GetDepth(viewportPoint.X, viewportPoint.Y);
        }

        /// <summary>
        /// Reads the stencil buffer value at the specified viewport position from the default framebuffer.
        /// Uses synchronous GPU readback which may cause pipeline stalls.
        /// </summary>
        /// <param name="viewportPoint">The viewport-local coordinates to sample.</param>
        /// <returns>The stencil buffer value (0-255).</returns>
        public static byte GetStencil(Vector2 viewportPoint)
        {
            State.UnbindFrameBuffers(EFramebufferTarget.ReadFramebuffer);
            State.SetReadBuffer(EReadBufferMode.None);
            return State.GetStencilIndex(viewportPoint.X, viewportPoint.Y);
        }

        /// <summary>
        /// Reads the depth buffer value at the specified viewport position from a specific framebuffer.
        /// Uses synchronous GPU readback which may cause pipeline stalls.
        /// </summary>
        /// <param name="fbo">The framebuffer to read depth from.</param>
        /// <param name="viewportPoint">The viewport-local coordinates to sample.</param>
        /// <returns>The depth buffer value.</returns>
        public static float GetDepth(XRFrameBuffer fbo, IVector2 viewportPoint)
        {
            using var t = fbo.BindForReadingState();
            State.SetReadBuffer(EReadBufferMode.None);
            return State.GetDepth(viewportPoint.X, viewportPoint.Y);
        }

        /// <summary>
        /// Asynchronously reads the depth buffer value at the specified viewport position.
        /// Uses pixel buffer objects (PBOs) to avoid blocking the CPU while waiting for GPU data.
        /// Preferred over GetDepth for performance-critical scenarios.
        /// </summary>
        /// <param name="fbo">The framebuffer to read depth from.</param>
        /// <param name="viewportPoint">The viewport-local coordinates to sample.</param>
        /// <returns>A task that completes with the depth buffer value.</returns>
        public static async Task<float> GetDepthAsync(XRFrameBuffer fbo, IVector2 viewportPoint)
            => await State.GetDepthAsync(fbo, viewportPoint.X, viewportPoint.Y);

        /// <summary>
        /// Reads the stencil buffer value at the specified viewport position from a specific framebuffer.
        /// Uses synchronous GPU readback which may cause pipeline stalls.
        /// </summary>
        /// <param name="fbo">The framebuffer to read stencil from.</param>
        /// <param name="viewportPoint">The viewport-local coordinates to sample.</param>
        /// <returns>The stencil buffer value (0-255).</returns>
        public static byte GetStencil(XRFrameBuffer fbo, Vector2 viewportPoint)
        {
            using var t = fbo.BindForReadingState();
            State.SetReadBuffer(EReadBufferMode.None);
            return State.GetStencilIndex(viewportPoint.X, viewportPoint.Y);
        }

        /// <summary>
        /// Creates a ray from the camera through the specified viewport position into the world.
        /// The ray originates at the camera position and extends through the screen point.
        /// Use for object picking, raycasting, and mouse interaction in 3D space.
        /// </summary>
        /// <param name="viewportPoint">The viewport-local coordinates in pixels.</param>
        /// <returns>A ray from the camera through the screen point.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no camera is set to this viewport.</exception>
        public Ray GetWorldRay(Vector2 viewportPoint)
            => _camera?.GetWorldRay(viewportPoint / _region.Size)
                ?? throw new InvalidOperationException("No camera is set to this viewport.");

        /// <summary>
        /// Creates a line segment from the camera's near plane to far plane through the specified viewport position.
        /// Similar to GetWorldRay but returns finite endpoints at the near/far planes.
        /// Useful for physics queries that require segment intersection tests.
        /// </summary>
        /// <param name="normalizedViewportPoint">The normalized [0,1] viewport coordinates.</param>
        /// <returns>A segment from near plane to far plane through the screen point, or zero segment if no camera.</returns>
        public Segment GetWorldSegment(Vector2 normalizedViewportPoint)
            => _camera?.GetWorldSegment(normalizedViewportPoint)
                ?? new Segment(Vector3.Zero, Vector3.Zero);

        //TODO: provide PickScene with a List<(XRComponent item, object? data)> pool to take from and release to. As few allocations as possible for constant picking every frame.

        //private readonly RayTraceClosest _closestPick = new(Vector3.Zero, Vector3.Zero, 0, 0xFFFF);

        /// <summary>
        /// Performs asynchronous scene picking at the specified viewport position.
        /// Tests against UI elements, scene octree geometry, and physics colliders as configured.
        /// Results are returned via callbacks, sorted by distance from camera.
        /// 
        /// This method supports multiple picking sources:
        /// - HUD: Tests screen-space UI interactable components
        /// - Octree: Tests renderable geometry using spatial octree acceleration
        /// - Physics: Tests physics colliders using the physics engine's raycast
        /// 
        /// Results are accumulated in the provided sorted dictionaries, keyed by distance.
        /// Multiple hits at the same distance are grouped in lists.
        /// </summary>
        /// <param name="normalizedViewportPosition">The normalized [0,1] viewport coordinates to pick at.</param>
        /// <param name="testHud">When true, tests against screen-space UI components.</param>
        /// <param name="testSceneOctree">When true, tests against renderable geometry in the spatial octree.</param>
        /// <param name="testScenePhysics">When true, tests against physics colliders.</param>
        /// <param name="layerMask">Bit mask specifying which physics layers to test against.</param>
        /// <param name="filter">Optional physics query filter for advanced filtering (e.g., exclude triggers).</param>
        /// <param name="orderedOctreeResults">Dictionary to receive octree hit results, sorted by distance.</param>
        /// <param name="orderedPhysicsResults">Dictionary to receive physics hit results, sorted by distance.</param>
        /// <param name="octreeFinishedCallback">Called when octree picking completes with the results dictionary.</param>
        /// <param name="physicsFinishedCallback">Called when physics picking completes with the results dictionary.</param>
        /// <param name="octreeHitMode">Determines how octree hits are detected (faces, bounds, etc.). Default: Faces.</param>
        /// <param name="ignored">Components to exclude from picking results.</param>
        public void PickSceneAsync(
            Vector2 normalizedViewportPosition,
            bool testHud,
            bool testSceneOctree,
            bool testScenePhysics,
            LayerMask layerMask,
            AbstractPhysicsScene.IAbstractQueryFilter? filter,
            SortedDictionary<float, List<(RenderInfo3D item, object? data)>> orderedOctreeResults,
            SortedDictionary<float, List<(XRComponent? item, object? data)>> orderedPhysicsResults,
            Action<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> octreeFinishedCallback,
            Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>?> physicsFinishedCallback,
            ERaycastHitMode octreeHitMode = ERaycastHitMode.Faces,
            params XRComponent[] ignored)
        {
            if (CameraComponent is null)
                return;

            if (testHud)
            {
                var cameraCanvas = CameraComponent.GetUserInterfaceOverlay();
                if (cameraCanvas is not null && cameraCanvas.CanvasTransform.DrawSpace != ECanvasDrawSpace.World)
                {
                    UIComponent?[] hudComps = cameraCanvas.FindDeepestComponents(normalizedViewportPosition);
                    foreach (var hudComp in hudComps)
                    {
                        if (hudComp is not UIInteractableComponent inter || !inter.IsActive)
                            continue;

                        float dist = 0.0f;
                        dist = CameraComponent.Camera.DistanceFrom(hudComp.Transform.WorldTranslation, false);
                        orderedPhysicsResults.Add(dist, [(inter, null)]);
                    }
                }
            }

            if (World is null)
                return;

            if (testSceneOctree)
            {
                World.RaycastOctreeAsync(
                    CameraComponent,
                    normalizedViewportPosition,
                    orderedOctreeResults,
                    octreeFinishedCallback,
                    octreeHitMode);
            }

            if (testScenePhysics)
            {
                World.RaycastPhysicsAsync(
                    CameraComponent,
                    normalizedViewportPosition,
                    layerMask,
                    filter,
                    orderedPhysicsResults,
                    physicsFinishedCallback);
            }
        }

        #endregion

        #region Viewport Layout

        /// <summary>
        /// Reconfigures this viewport's position for a new total viewport count.
        /// Automatically selects the appropriate split-screen layout based on:
        /// - Total number of viewports (1-4 supported)
        /// - This viewport's index within that set
        /// - User preferences for 2-player and 3-player layouts
        /// 
        /// Call this when viewports are added/removed to update the layout.
        /// After calling, use Resize() to apply the new percentages to actual pixel dimensions.
        /// </summary>
        /// <param name="newIndex">This viewport's new index (0 = first/primary viewport).</param>
        /// <param name="total">Total number of viewports to divide the screen between.</param>
        /// <param name="twoPlayerPref">User preference for 2-player layout (horizontal or vertical split).</param>
        /// <param name="threePlayerPref">User preference for 3-player layout (which viewport gets extra space).</param>
        public void ViewportCountChanged(int newIndex, int total, ETwoPlayerPreference twoPlayerPref, EThreePlayerPreference threePlayerPref)
        {
            Index = newIndex;
            switch (total)
            {
                case 1:
                    SetFullScreen();
                    break;
                case 2:
                    switch (newIndex)
                    {
                        case 0:
                            if (twoPlayerPref == ETwoPlayerPreference.SplitHorizontally)
                                SetTop();
                            else
                                SetLeft();
                            break;
                        case 1:
                            if (twoPlayerPref == ETwoPlayerPreference.SplitHorizontally)
                                SetBottom();
                            else
                                SetRight();
                            break;
                    }
                    break;
                case 3:
                    switch (newIndex)
                    {
                        case 0:
                            switch (threePlayerPref)
                            {
                                case EThreePlayerPreference.BlankBottomRight:
                                    SetTopLeft();
                                    break;
                                case EThreePlayerPreference.PreferFirstPlayer:
                                    SetTop();
                                    break;
                                case EThreePlayerPreference.PreferSecondPlayer:
                                    SetBottomLeft();
                                    break;
                                case EThreePlayerPreference.PreferThirdPlayer:
                                    SetTopLeft();
                                    break;
                            }
                            break;
                        case 1:
                            switch (threePlayerPref)
                            {
                                case EThreePlayerPreference.BlankBottomRight:
                                    SetTopRight();
                                    break;
                                case EThreePlayerPreference.PreferFirstPlayer:
                                    SetBottomLeft();
                                    break;
                                case EThreePlayerPreference.PreferSecondPlayer:
                                    SetTop();
                                    break;
                                case EThreePlayerPreference.PreferThirdPlayer:
                                    SetTopRight();
                                    break;
                            }
                            break;
                        case 2:
                            switch (threePlayerPref)
                            {
                                case EThreePlayerPreference.BlankBottomRight:
                                    SetBottomLeft();
                                    break;
                                case EThreePlayerPreference.PreferFirstPlayer:
                                    SetBottomRight();
                                    break;
                                case EThreePlayerPreference.PreferSecondPlayer:
                                    SetBottomRight();
                                    break;
                                case EThreePlayerPreference.PreferThirdPlayer:
                                    SetBottom();
                                    break;
                            }
                            break;
                    }
                    break;
                case 4:
                    switch (newIndex)
                    {
                        case 0: SetTopLeft(); break;
                        case 1: SetTopRight(); break;
                        case 2: SetBottomLeft(); break;
                        case 3: SetBottomRight(); break;
                    }
                    break;
            }
        }

        /// <summary>
        /// Configures this viewport to cover the entire window.
        /// Sets percentage values to span from (0,0) to (1,1).
        /// Call Resize() after this to apply the new layout.
        /// </summary>
        public void SetFullScreen()
        {
            _leftPercentage = _bottomPercentage = 0.0f;
            _rightPercentage = _topPercentage = 1.0f;
        }

        /// <summary>
        /// Configures this viewport for the top half of a horizontal split.
        /// Spans full width, from 50% to 100% height.
        /// Used in 2-player horizontal split layouts.
        /// </summary>
        public void SetTop()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 1.0f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.5f;
        }

        /// <summary>
        /// Configures this viewport for the bottom half of a horizontal split.
        /// Spans full width, from 0% to 50% height.
        /// Used in 2-player horizontal split layouts.
        /// </summary>
        public void SetBottom()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 1.0f;
            _topPercentage = 0.5f;
            _bottomPercentage = 0.0f;
        }

        /// <summary>
        /// Configures this viewport for the left half of a vertical split.
        /// Spans full height, from 0% to 50% width.
        /// Used in 2-player vertical split layouts.
        /// </summary>
        public void SetLeft()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 0.5f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.0f;
        }

        /// <summary>
        /// Configures this viewport for the right half of a vertical split.
        /// Spans full height, from 50% to 100% width.
        /// Used in 2-player vertical split layouts.
        /// </summary>
        public void SetRight()
        {
            _leftPercentage = 0.5f;
            _rightPercentage = 1.0f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.0f;
        }

        /// <summary>
        /// Configures this viewport for the top-left quadrant.
        /// Spans 0-50% width and 50-100% height.
        /// Used in 3-player and 4-player layouts.
        /// </summary>
        public void SetTopLeft()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 0.5f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.5f;
        }

        /// <summary>
        /// Configures this viewport for the top-right quadrant.
        /// Spans 50-100% width and 50-100% height.
        /// Used in 3-player and 4-player layouts.
        /// </summary>
        public void SetTopRight()
        {
            _leftPercentage = 0.5f;
            _rightPercentage = 1.0f;
            _topPercentage = 1.0f;
            _bottomPercentage = 0.5f;
        }

        /// <summary>
        /// Configures this viewport for the bottom-left quadrant.
        /// Spans 0-50% width and 0-50% height.
        /// Used in 3-player and 4-player layouts.
        /// </summary>
        public void SetBottomLeft()
        {
            _leftPercentage = 0.0f;
            _rightPercentage = 0.5f;
            _topPercentage = 0.5f;
            _bottomPercentage = 0.0f;
        }

        /// <summary>
        /// Configures this viewport for the bottom-right quadrant.
        /// Spans 50-100% width and 0-50% height.
        /// Used in 3-player and 4-player layouts.
        /// </summary>
        public void SetBottomRight()
        {
            _leftPercentage = 0.5f;
            _rightPercentage = 1.0f;
            _topPercentage = 0.5f;
            _bottomPercentage = 0.0f;
        }

        #endregion
    }
}
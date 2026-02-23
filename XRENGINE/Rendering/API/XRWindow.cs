using Extensions;
using Newtonsoft.Json;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Input;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering
{
    /// <summary>
    /// Links a Silk.NET generated window to an API-specific engine renderer.
    /// </summary>
    [RuntimeOnly]
    public sealed class XRWindow : XRBase, IDisposable
    {
        #region Nested Types

        private class NodeRepresentation
        {
            public Guid ServerGUID { get; set; }
            public (string? FullTypeDef, Guid ServerGUID) TransformType { get; set; } = (null, Guid.Empty);
            public (string FullTypeDef, Guid ServerGUID)[] ComponentTypes { get; set; } = [];
            public NodeRepresentation[] Children { get; set; } = [];
        }

        private class WorldHierarchy
        {
            public string? GameModeFullTypeDef { get; set; }
            public NodeRepresentation?[]? RootNodes { get; set; } = [];
        }

        #endregion

        #region Events

        public static event Action<XRWindow, bool>? AnyWindowFocusChanged;
        public event Action<XRWindow, bool>? FocusChanged;
        public event Action? RenderViewportsCallback;
        public event Action? PostRenderViewportsCallback;

        #endregion

        #region Fields

        private readonly EventList<XRViewport> _viewports = [];
        private XRWorldInstance? _targetWorldInstance;
        private bool _isFocused = false;

        // Editor scene-panel presentation (dockable viewport panel) adapter.
        private readonly XRWindowScenePanelAdapter _scenePanelAdapter = new();

        #endregion

        private bool _isDisposing;
        private bool _isDisposed;

        private Exception? _lastRenderException;
        private int _consecutiveRenderFailures;
        private DateTime _renderDisabledUntilUtc;

        private bool _rendererInitialized;

        #region Properties

        /// <summary>
        /// Silk.NET window instance.
        /// </summary>
        public IWindow Window { get; }

        /// <summary>
        /// Interface to render a scene for this window using the requested graphics API.
        /// </summary>
        public AbstractRenderer Renderer { get; }

        public IInputContext? Input { get; private set; }

        public XRWorldInstance? TargetWorldInstance
        {
            get => _targetWorldInstance;
            set => SetField(ref _targetWorldInstance, value);
        }

        /// <summary>
        /// Indicates whether this window prefers HDR output; renderers can override swap-chain/context setup accordingly.
        /// </summary>
        public bool PreferHDROutput { get; internal set; }

        /// <summary>
        /// True when the platform's native chrome should remain visible. False means the engine is expected to render its own title bar.
        /// </summary>
        public bool UseNativeTitleBar { get; }

        public EventList<XRViewport> Viewports => _viewports;

        public bool IsTickLinked { get; private set; } = false;

        public bool IsFocused
        {
            get => _isFocused;
            private set => SetField(ref _isFocused, value);
        }

        /// <summary>
        /// Gets the texture containing the rendered scene for dockable editor scene-panel presentation.
        /// </summary>
        public XRTexture2D? ScenePanelTexture => _scenePanelAdapter.Texture;

        /// <summary>
        /// Gets the FBO used for rendering in dockable editor scene-panel presentation.
        /// </summary>
        public XRFrameBuffer? ScenePanelFrameBuffer => _scenePanelAdapter.FrameBuffer;


        #endregion

        public bool IsDisposed => _isDisposed;

        public Exception? LastRenderException => _lastRenderException;

        public int ConsecutiveRenderFailures => _consecutiveRenderFailures;

        public DateTime? RenderDisabledUntilUtc
            => _renderDisabledUntilUtc == default ? null : _renderDisabledUntilUtc;

        public bool IsRenderTemporarilyDisabled
            => RenderDisabledUntilUtc is DateTime until && DateTime.UtcNow < until;

        public TimeSpan? RenderDisableRemaining
        {
            get
            {
                DateTime? until = RenderDisabledUntilUtc;
                if (until is null)
                    return null;

                TimeSpan remaining = until.Value - DateTime.UtcNow;
                return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
            }
        }

        public void ResetRenderCircuitBreaker()
        {
            _consecutiveRenderFailures = 0;
            _renderDisabledUntilUtc = default;
            _lastRenderException = null;
        }

        /// <summary>
        /// Forces the window to re-evaluate whether it should be tick-linked and rendering.
        /// Intended for settings/UI changes that don't touch Viewports/TargetWorldInstance.
        /// </summary>
        public void RequestRenderStateRecheck(bool resetCircuitBreaker = false)
        {
            if (_isDisposed || _isDisposing)
                return;

            if (resetCircuitBreaker)
                ResetRenderCircuitBreaker();

            VerifyTick();
        }

        /// <summary>
        /// Destroys viewport-panel mode GPU resources so they are recreated on demand.
        /// Useful after swapchain/framebuffer invalidation or device/context transitions.
        /// </summary>
        public void InvalidateScenePanelResources()
        {
            if (_isDisposed || _isDisposing)
                return;

            _scenePanelAdapter.InvalidateResources();
        }

        #region Constructor

        public XRWindow(WindowOptions options, bool useNativeTitleBar)
        {
            _viewports.CollectionChanged += ViewportsChanged;

            Silk.NET.Windowing.Window.PrioritizeGlfw();
            Window = Silk.NET.Windowing.Window.Create(options);
            UseNativeTitleBar = useNativeTitleBar;

            LinkWindow();
            Window.Initialize();

            Renderer = Window.API.API switch
            {
                ContextAPI.OpenGL => new OpenGLRenderer(this, true),
                ContextAPI.Vulkan => new VulkanRenderer(this, true),
                _ => throw new Exception($"Unsupported API: {Window.API.API}"),
            };
        }

        #endregion

        #region Property Changed

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(TargetWorldInstance):
                    VerifyTick();
                    if (!(Engine.Networking?.IsClient ?? false))
                        Engine.Networking?.ReplicateStateChange(
                            new Engine.StateChangeInfo(
                                Engine.EStateChangeType.WorldChange,
                                JsonConvert.SerializeObject(EncodeWorldHierarchy())),
                            true,
                            true);
                    break;
            }
        }

        #endregion

        #region Public Methods - World Management

        public void SetWorld(XRWorld? targetWorld)
        {
            if (targetWorld is not null)
                TargetWorldInstance = XRWorldInstance.GetOrInitWorld(targetWorld);
        }

        #endregion

        #region Public Methods - Viewport Management

        public XRViewport GetOrAddViewportForPlayer(LocalPlayerController controller, bool autoSizeAllViewports)
            => controller.Viewport ??= AddViewportForPlayer(controller, autoSizeAllViewports);

        /// <summary>
        /// Remakes all viewports in order of active local player indices.
        /// </summary>
        public void ResizeAllViewportsAccordingToPlayers()
        {
            using var sample = Engine.Profiler.Start("XRWindow.ResizeAllViewportsAccordingToPlayers");

            LocalPlayerController[] players = [.. Viewports
                .Select(x => x.AssociatedPlayer)
                .OfType<LocalPlayerController>()
                .Distinct()
                .OrderBy(x => (int)x.LocalPlayerIndex)];
            foreach (var viewport in Viewports)
                viewport.Destroy();
            Viewports.Clear();
            for (int i = 0; i < players.Length; i++)
                AddViewportForPlayer(players[i], false);
        }

        public void UpdateViewportSizes()
        {
            using var sample = Engine.Profiler.Start("XRWindow.UpdateViewportSizes");
            ResizeViewports(Window.Size);
        }

        #endregion

        #region Public Methods - Player Registration

        public void RegisterLocalPlayer(ELocalPlayerIndex playerIndex, bool autoSizeAllViewports)
            => RegisterController(Engine.State.GetOrCreateLocalPlayer(playerIndex), autoSizeAllViewports);

        public void RegisterController(LocalPlayerController controller, bool autoSizeAllViewports)
            => GetOrAddViewportForPlayer(controller, autoSizeAllViewports).AssociatedPlayer = controller;

        /// <summary>
        /// Ensures the given controller is registered with this window and has a valid viewport.
        /// This is more defensive than <see cref="RegisterController"/> and is intended for
        /// scenarios like snapshot restore where runtime-only references (controller.Viewport,
        /// viewport.AssociatedPlayer) can become stale or inconsistent.
        /// </summary>
        public XRViewport? EnsureControllerRegistered(LocalPlayerController controller, bool autoSizeAllViewports)
        {
            if (controller is null)
                return null;

            // If the controller is holding a stale viewport reference (not owned by this window), drop it.
            if (controller.Viewport is not null && !Viewports.Contains(controller.Viewport))
                controller.Viewport = null;

            // Prefer an existing viewport already tied to the same local player index.
            var existingByIndex = Viewports.FirstOrDefault(vp => vp.AssociatedPlayer?.LocalPlayerIndex == controller.LocalPlayerIndex);
            if (existingByIndex is not null)
            {
                existingByIndex.AssociatedPlayer = controller;
                controller.Viewport = existingByIndex;  // CRITICAL: bind controller to viewport
                return existingByIndex;
            }

            // Otherwise, reuse an unassigned viewport if one exists.
            var unassigned = Viewports.FirstOrDefault(vp => vp.AssociatedPlayer is null);
            if (unassigned is not null)
            {
                unassigned.AssociatedPlayer = controller;
                controller.Viewport = unassigned;  // CRITICAL: bind controller to viewport
                return unassigned;
            }

            // Fallback: create a viewport for the controller.
            RegisterController(controller, autoSizeAllViewports);
            return controller.Viewport;
        }

        public void UnregisterLocalPlayer(ELocalPlayerIndex playerIndex)
        {
            LocalPlayerController? controller = Engine.State.GetLocalPlayer(playerIndex);
            if (controller is not null)
                UnregisterController(controller);
        }

        public void UnregisterController(LocalPlayerController controller)
        {
            if (controller.Viewport != null && Viewports.Contains(controller.Viewport))
                controller.Viewport = null;
        }

        #endregion

        #region Public Methods - Rendering

        public void RenderViewports()
        {
            using var sample = Engine.Profiler.Start("XRWindow.RenderViewports");
            foreach (var viewport in Viewports)
            {
                using var viewportSample = Engine.Profiler.Start($"XRViewport.Render[{viewport.Index}]");
                viewport.Render();
            }
        }

        /// <summary>
        /// Renders all viewports to the specified FBO instead of the window framebuffer.
        /// </summary>
        public void RenderViewportsToFBO(XRFrameBuffer? targetFBO)
        {
            if (targetFBO is null)
            {
                RenderViewports();
                return;
            }

            using var sample = Engine.Profiler.Start("XRWindow.RenderViewportsToFBO");
            foreach (var viewport in Viewports)
            {
                using var viewportSample = Engine.Profiler.Start($"XRViewport.RenderToFBO[{viewport.Index}]");
                viewport.Render(targetFBO);
            }
        }

        #endregion

        #region Window Event Handlers

        private void Window_Load()
        {
            //Task.Run(() =>
            //{
                Input = Window.CreateInput();
                Input.ConnectionChanged += Input_ConnectionChanged;
            //});
        }

        private void Window_Closing()
        {
            if (!_isDisposing && !_isDisposed && Engine.WindowCloseRequested is not null)
            {
                var decision = Engine.WindowCloseRequested.Invoke(this);
                if (decision != Engine.WindowCloseRequestResult.Allow)
                {
                    if (TryCancelCloseRequest())
                        return;
                }
            }

            try
            {
                Dispose();
            }
            finally
            {
                Engine.RemoveWindow(this);
            }
        }

        private bool TryCancelCloseRequest()
        {
            if (Window is null)
                return false;

            var windowType = Window.GetType();

            if (TrySetBoolProperty(windowType, Window, "IsClosing", false))
                return true;
            if (TrySetBoolProperty(windowType, Window, "ShouldClose", false))
                return true;
            if (TrySetBoolProperty(windowType, Window, "CloseRequested", false))
                return true;

            if (TrySetBoolField(windowType, Window, "_isClosing", false))
                return true;
            if (TrySetBoolField(windowType, Window, "_shouldClose", false))
                return true;
            if (TrySetBoolField(windowType, Window, "_closeRequested", false))
                return true;

            if (TryInvokeBoolSetter(windowType, Window, "SetShouldClose", false))
                return true;
            if (TryInvokeBoolSetter(windowType, Window, "SetCloseRequested", false))
                return true;
            if (TryInvokeParameterless(windowType, Window, "CancelClose"))
                return true;

            return false;
        }

        private static bool TrySetBoolProperty(Type type, object instance, string name, bool value)
        {
            try
            {
                var prop = type.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop is { CanWrite: true } && prop.PropertyType == typeof(bool))
                {
                    prop.SetValue(instance, value);
                    return true;
                }
            }
            catch
            {
                // ignored
            }

            return false;
        }

        private static bool TrySetBoolField(Type type, object instance, string name, bool value)
        {
            try
            {
                var field = type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (field is not null && field.FieldType == typeof(bool))
                {
                    field.SetValue(instance, value);
                    return true;
                }
            }
            catch
            {
                // ignored
            }

            return false;
        }

        private static bool TryInvokeBoolSetter(Type type, object instance, string name, bool value)
        {
            try
            {
                var method = type.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                if (method is not null)
                {
                    method.Invoke(instance, new object[] { value });
                    return true;
                }
            }
            catch
            {
                // ignored
            }

            return false;
        }

        private static bool TryInvokeParameterless(Type type, object instance, string name)
        {
            try
            {
                var method = type.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (method is not null)
                {
                    method.Invoke(instance, null);
                    return true;
                }
            }
            catch
            {
                // ignored
            }

            return false;
        }

        private void OnFocusChanged(bool focused)
        {
            IsFocused = focused;
            FocusChanged?.Invoke(this, focused);
            AnyWindowFocusChanged?.Invoke(this, focused);
        }

        private void FramebufferResizeCallback(Vector2D<int> obj)
        {
            using var sample = Engine.Profiler.Start("XRWindow.FramebufferResize");

            //Debug.Out("Window resized to {0}x{1}", obj.X, obj.Y);
            Viewports.ForEach(vp => vp.Resize((uint)obj.X, (uint)obj.Y, true));

            _scenePanelAdapter.OnFramebufferResized(this, obj);

            Renderer.FrameBufferInvalidated();

            //var timer = Engine.Time.Timer;
            //await timer.DispatchCollectVisible();
            //await timer.DispatchSwapBuffers();
            //timer.DispatchRender();
        }

        private void Input_ConnectionChanged(IInputDevice device, bool connected)
        {
            switch (device)
            {
                case IKeyboard keyboard:
                    break;
                case IMouse mouse:
                    break;
                case IGamepad gamepad:
                    break;
            }
        }

        #endregion

        #region Window Linking

        private void LinkWindow()
        {
            IWindow? w = Window;
            if (w is null)
                return;

            w.FramebufferResize += FramebufferResizeCallback;
            w.Render += RenderCallback;
            w.FocusChanged += OnFocusChanged;
            w.Closing += Window_Closing;
            w.Load += Window_Load;

            // Subscribe to play mode transitions to invalidate scene panel resources
            Engine.PlayMode.PreEnterPlay += OnPlayModeTransition;
            Engine.PlayMode.PostExitPlay += OnPlayModeTransition;
        }

        private void UnlinkWindow()
        {
            var w = Window;
            if (w is null)
                return;

            w.FramebufferResize -= FramebufferResizeCallback;
            w.Render -= RenderCallback;
            w.FocusChanged -= OnFocusChanged;
            w.Closing -= Window_Closing;
            w.Load -= Window_Load;

            // Unsubscribe from play mode events
            Engine.PlayMode.PreEnterPlay -= OnPlayModeTransition;
            Engine.PlayMode.PostExitPlay -= OnPlayModeTransition;
        }

        private void OnPlayModeTransition()
        {
            Debug.Out($"[XRWindow] OnPlayModeTransition called. PlayModeState={Engine.PlayMode.State} Viewports={Viewports.Count}");
            
            // Invalidate scene panel resources IMMEDIATELY so stale textures don't persist.
            // Using immediate destruction ensures the GL texture handle is invalidated before
            // ImGui tries to display it on the next frame.
            _scenePanelAdapter.InvalidateResourcesImmediate();

            // Also destroy all viewport render pipeline caches to ensure stale textures/FBOs
            // from the previous play mode state don't persist into the new state.
            foreach (var viewport in Viewports)
            {
                Debug.Out($"[XRWindow] Destroying pipeline cache for VP[{viewport.Index}] CameraComponent={viewport.CameraComponent?.Name ?? "<null>"} ActiveCamera={viewport.ActiveCamera?.GetHashCode().ToString() ?? "null"}");
                viewport.RenderPipelineInstance.DestroyCache();
            }

            // Some rendering state (viewport size/internal resolution/aspect ratio) is only recomputed
            // on resize events. Play mode transitions can invalidate cached GPU resources without
            // any actual OS resize, leaving the window presenting stale/incorrect content until the
            // user manually resizes a panel/window.
            //
            // Force a sizing refresh against the current framebuffer dimensions to mimic that resize.
            var fb = Window?.FramebufferSize ?? default;
            if (fb.X > 0 && fb.Y > 0)
            {
                foreach (var viewport in Viewports)
                    viewport.Resize((uint)fb.X, (uint)fb.Y, setInternalResolution: true);

                // Notify renderer that cached framebuffer-dependent objects may be invalid.
                Renderer.FrameBufferInvalidated();
            }
        }

        #endregion

        #region Tick Management

        private void VerifyTick()
        {
            using var sample = Engine.Profiler.Start("XRWindow.VerifyTick");

            if (_isDisposed || _isDisposing)
                return;

            if (ShouldBeRendering())
            {
                if (IsTickLinked)
                    return;

                IsTickLinked = true;
                BeginTick();
            }
            else
            {
                if (!IsTickLinked)
                    return;

                IsTickLinked = false;
                EndTick();
            }
        }

        private void BeginTick()
        {
            using var sample = Engine.Profiler.Start("XRWindow.BeginTick");

            Renderer.Initialize();
            _rendererInitialized = true;
            Engine.Time.Timer.SwapBuffers += SwapBuffers;
            Engine.Time.Timer.RenderFrame += RenderFrame;
        }

        private void EndTick()
        {
            using var sample = Engine.Profiler.Start("XRWindow.EndTick");

            Engine.Time.Timer.SwapBuffers -= SwapBuffers;
            Engine.Time.Timer.RenderFrame -= RenderFrame;
            Renderer.WaitForGpu();
            Renderer.DestroyCachedAPIRenderObjects();
            Engine.Rendering.DestroyObjectsForRenderer(Renderer);
            Renderer.CleanUp();
            _rendererInitialized = false;
            Window.DoEvents();
        }

        private void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("XRWindow.SwapBuffers");
        }

        private void RenderFrame()
        {
            // Guard against rendering after window is disposed or GL context is invalid
            if (_isDisposed || _isDisposing)
                return;

            using var sample = Engine.Profiler.Start("XRWindow.Timer.RenderFrame");

            {
                using var eventsSample = Engine.Profiler.Start("XRWindow.Timer.DoEvents");
                Window.DoEvents();
            }

            // Re-check after DoEvents in case window was closed
            if (_isDisposed || _isDisposing)
                return;

            {
                using var doRenderSample = Engine.Profiler.Start("XRWindow.Timer.DoRender");
                Window.DoRender();
            }
        }

        private bool ShouldBeRendering()
            => !_isDisposed && !_isDisposing && Viewports.Count > 0 && TargetWorldInstance is not null;

        #endregion

        #region Render Callback

        //private float _lastFrameTime = 0.0f;
        private void RenderCallback(double delta)
        {
            if (_isDisposed || _isDisposing)
                return;

            if (_renderDisabledUntilUtc != default && DateTime.UtcNow < _renderDisabledUntilUtc)
                return;

            using var frameSample = Engine.Profiler.Start("XRWindow.RenderFrame");

            // Reset per-frame rendering statistics at the start of each frame
            Engine.Rendering.Stats.BeginFrame();

            // Process any pending async buffer uploads within the frame budget
            using (var uploadSample = Engine.Profiler.Start("XRWindow.ProcessPendingUploads"))
            {
                Renderer.ProcessPendingUploads();
            }

            try
            {
                Renderer.Active = true;
                AbstractRenderer.Current = Renderer;

                bool useScenePanelMode =
                    Engine.IsEditor &&
                    Engine.EditorPreferences.ViewportPresentationMode == EditorPreferences.EViewportPresentationMode.UseViewportPanel;
                bool forceFullViewport = string.Equals(
                    Environment.GetEnvironmentVariable("XRE_FORCE_FULL_VIEWPORT"),
                    "1",
                    StringComparison.Ordinal);
                if (forceFullViewport)
                    useScenePanelMode = false;
                bool mirrorByComposition =
                    Engine.VRState.IsInVR &&
                    Engine.Rendering.Settings.RenderWindowsWhileInVR &&
                    Engine.Rendering.Settings.VrMirrorComposeFromEyeTextures;
                bool canRenderWindowViewports =
                    !Engine.VRState.IsInVR ||
                    (Engine.Rendering.Settings.RenderWindowsWhileInVR && !mirrorByComposition);

                LogRenderDiagnostics(delta, useScenePanelMode, canRenderWindowViewports, forceFullViewport);
                ApplyForcedDebugOpaquePipelineOverride();

                using (var preRenderSample = Engine.Profiler.Start("XRWindow.GlobalPreRender"))
                {
                    try
                    {
                        TargetWorldInstance?.GlobalPreRender();
                    }
                    catch (Exception preRenderEx)
                    {
                        string keyBase = $"XRWindow.RenderCallback.{GetHashCode()}";
                        Debug.RenderingWarningEvery(
                            keyBase + ".GlobalPreRenderException",
                            TimeSpan.FromSeconds(1),
                            "[RenderDiag] GlobalPreRender failed (Vulkan present will still run). {0}",
                            preRenderEx);
                    }
                }

                using (var renderCallbackSample = Engine.Profiler.Start("XRWindow.RenderViewportsCallback"))
                {
                    RenderViewportsCallback?.Invoke();
                }

                // Viewport/pipeline rendering is isolated so that exceptions during scene rendering
                // do not prevent Vulkan's WindowRenderCallback (acquire/record/submit/present) from
                // executing. In OpenGL the window swap is handled by Silk.NET automatically, but
                // Vulkan requires explicit present — skipping it leaves the window uninitialized (white).
                bool viewportRenderFailed = false;
                try
                {
                    RenderWindowViewports(useScenePanelMode, canRenderWindowViewports, mirrorByComposition);
                }
                catch (Exception vpEx)
                {
                    viewportRenderFailed = true;
                    string keyBase = $"XRWindow.RenderCallback.{GetHashCode()}";
                    Debug.RenderingWarningEvery(
                        keyBase + ".ViewportException",
                        TimeSpan.FromSeconds(1),
                        "[RenderDiag] Viewport/pipeline rendering failed (Vulkan present will still run). {0}",
                        vpEx);
                }

                using (var postRenderSample = Engine.Profiler.Start("XRWindow.GlobalPostRender"))
                {
                    try
                    {
                        TargetWorldInstance?.GlobalPostRender();
                    }
                    catch (Exception postRenderEx)
                    {
                        string keyBase = $"XRWindow.RenderCallback.{GetHashCode()}";
                        Debug.RenderingWarningEvery(
                            keyBase + ".GlobalPostRenderException",
                            TimeSpan.FromSeconds(1),
                            "[RenderDiag] GlobalPostRender failed (Vulkan present will still run). {0}",
                            postRenderEx);
                    }
                }

                // Allow the renderer to perform any per-window end-of-frame work (e.g., Vulkan acquire/submit/present).
                // This MUST run even when viewport rendering fails, otherwise Vulkan never presents and the window
                // shows uninitialized (white) content. With empty/partial frame ops, the Vulkan backend will at
                // minimum clear to the background color and render the debug triangle + ImGui overlay.
                Renderer.RenderWindow(delta);

                using (var postViewportsSample = Engine.Profiler.Start("XRWindow.PostRenderViewportsCallback"))
                {
                    PostRenderViewportsCallback?.Invoke();
                }

                // Successful frame: clear circuit breaker state (viewport failures don't block present).
                if (!viewportRenderFailed)
                {
                    _consecutiveRenderFailures = 0;
                    _renderDisabledUntilUtc = default;
                }
            }
            catch (Exception ex)
            {
                _lastRenderException = ex;
                _consecutiveRenderFailures++;

                // Simple circuit breaker to avoid exception spam + runaway per-frame failures.
                // Backoff grows up to 5 seconds.
                int backoffMs = Math.Min(5000, 100 * _consecutiveRenderFailures);
                _renderDisabledUntilUtc = DateTime.UtcNow.AddMilliseconds(backoffMs);

                string keyBase = $"XRWindow.RenderCallback.{GetHashCode()}";
                Debug.RenderingWarningEvery(
                    keyBase + ".Exception",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Render exception (disabled {0}ms, failures={1}). {2}",
                    backoffMs,
                    _consecutiveRenderFailures,
                    ex);
            }
            finally
            {
                Renderer.Active = false;
                AbstractRenderer.Current = null;
            }
        }

        private void ApplyForcedDebugOpaquePipelineOverride()
        {
            bool forceDebugOpaque = string.Equals(
                Environment.GetEnvironmentVariable("XRE_FORCE_DEBUG_OPAQUE_PIPELINE"),
                "1",
                StringComparison.Ordinal);
            if (!forceDebugOpaque)
                return;

            foreach (var viewport in Viewports)
            {
                if (viewport.RenderPipeline is DebugOpaqueRenderPipeline)
                    continue;

                viewport.RenderPipeline = new DebugOpaqueRenderPipeline();
                Debug.RenderingEvery(
                    $"XRWindow.ForceDebugOpaque.{GetHashCode()}.{viewport.Index}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDiag] Forced DebugOpaqueRenderPipeline for VP[{0}] due to XRE_FORCE_DEBUG_OPAQUE_PIPELINE=1.",
                    viewport.Index);
            }
        }

        private void LogRenderDiagnostics(double delta, bool useScenePanelMode, bool canRenderWindowViewports, bool forceFullViewport)
        {
            string keyBase = $"XRWindow.RenderCallback.{GetHashCode()}";

/*
            Debug.RenderingEvery(
                keyBase + ".Mode",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] Mode: PanelMode={0} ForcedFull={1} Pref={2} CanRender={3} Viewports={4} TargetWorldNull={5} Delta={6:F4}",
                useScenePanelMode,
                forceFullViewport,
                Engine.EditorPreferences.ViewportPresentationMode,
                canRenderWindowViewports,
                Viewports.Count,
                TargetWorldInstance is null,
                delta);
*/

            if (!canRenderWindowViewports)
            {
                Debug.RenderingEvery(
                    keyBase + ".VRGated",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Window gated by VR. IsInVR={0}, RenderWindowsWhileInVR={1}",
                    Engine.VRState.IsInVR,
                    Engine.Rendering.Settings.RenderWindowsWhileInVR);
            }

            if (!ShouldBeRendering())
            {
                Debug.RenderingWarningEvery(
                    keyBase + ".NotRendering",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Window not rendering: Viewports={0}, TargetWorldInstanceNull={1}, PresentationMode={2}, CanRenderWindowViewports={3}",
                    Viewports.Count,
                    TargetWorldInstance is null,
                    Engine.EditorPreferences.ViewportPresentationMode,
                    canRenderWindowViewports);
            }

            // Per-viewport state snapshot (only a few times a second)
            /*
            Debug.RenderingEvery(
                keyBase + ".ViewportSummary",
                TimeSpan.FromSeconds(2),
                "[RenderDiag] Window tick. Delta={0:F4}s, Viewports={1}, TargetWorld={2}, PanelMode={3}",
                delta,
                Viewports.Count,
                TargetWorldInstance?.TargetWorld?.Name ?? "<null>",
                useScenePanelMode);
            */
            
            foreach (var vp in Viewports)
            {
                var activeCamera = vp.ActiveCamera;
                var world = vp.World;
                bool hasPipeline = vp.RenderPipelineInstance.Pipeline is not null;

                /*
                Debug.RenderingEvery(
                    keyBase + $".VP.{vp.Index}",
                    TimeSpan.FromSeconds(2),
                    "[RenderDiag] VP[{0}] Region={1}x{2}@({3},{4}) Internal={5}x{6} ActiveCameraNull={7} WorldNull={8} PipelineNull={9} AssocPlayer={10}",
                    vp.Index,
                    vp.Width,
                    vp.Height,
                    vp.X,
                    vp.Y,
                    vp.InternalWidth,
                    vp.InternalHeight,
                    activeCamera is null,
                    world is null,
                    !hasPipeline,
                    vp.AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>");
                */
            }
        }

        private void RenderWindowViewports(bool useScenePanelMode, bool canRenderWindowViewports, bool mirrorByComposition)
        {
            if (canRenderWindowViewports)
            {
                if (useScenePanelMode)
                {
                    bool renderedInPanel = _scenePanelAdapter.TryRenderScenePanelMode(this);
                    if (!renderedInPanel)
                    {
                        _scenePanelAdapter.EndScenePanelMode(this);

                        // If panel presentation is enabled but the panel region is unavailable
                        // (e.g., panel hidden/closed or not yet laid out), keep rendering the
                        // scene directly to the window instead of skipping world rendering.
                        Renderer.SetCroppingEnabled(false);
                        RenderViewports();
                    }
                }
                else
                {
                    _scenePanelAdapter.EndScenePanelMode(this);

                    // RenderViewportsCallback (e.g., ImGui) can leave scissor/cropping enabled.
                    // Ensure world rendering starts from a clean state so clears and passes aren't clipped/offset.
                    Renderer.SetCroppingEnabled(false);

                    RenderViewports();
                }
            }
            else
            {
                _scenePanelAdapter.EndScenePanelMode(this);

                if (mirrorByComposition)
                {
                    var fb = Window.FramebufferSize;
                    uint targetWidth = (uint)Math.Max(1, fb.X);
                    uint targetHeight = (uint)Math.Max(1, fb.Y);
                    _ = Engine.VRState.OpenXRApi?.TryRenderDesktopMirrorComposition(targetWidth, targetHeight);
                }
            }
        }

        #endregion

        #region Viewport Collection Management

        private void ViewportsChanged(object sender, TCollectionChangedEventArgs<XRViewport> e)
        {
            switch (e.Action)
            {
                case ECollectionChangedAction.Remove:
                    foreach (var viewport in e.OldItems)
                        viewport.Destroy();
                    break;
                case ECollectionChangedAction.Clear:
                    foreach (var viewport in e.OldItems)
                        viewport.Destroy();
                    break;
            }
            VerifyTick();
        }

        private XRViewport AddViewportForPlayer(LocalPlayerController? controller, bool autoSizeAllViewports)
        {
            using var sample = Engine.Profiler.Start("XRWindow.AddViewportForPlayer");

            XRViewport newViewport = XRViewport.ForTotalViewportCount(this, Viewports.Count);
            newViewport.AssociatedPlayer = controller;
            Viewports.Add(newViewport);

            Debug.Out("Added new viewport to {0}: {1}", GetType().GetFriendlyName(), newViewport.Index);

            Debug.Out(
                "[ViewportDiag] XRWindow.AddViewportForPlayer: WinHash={0} VP[{1}] VPHash={2} AssocCtrlHash={3} AssocIndex={4} Ctrl.ViewportHash={5}",
                GetHashCode(),
                newViewport.Index,
                newViewport.GetHashCode(),
                controller?.GetHashCode() ?? 0,
                controller is null ? "<null>" : $"P{(int)controller.LocalPlayerIndex + 1}",
                controller?.Viewport?.GetHashCode() ?? 0);

            if (autoSizeAllViewports)
                ResizeAllViewportsAccordingToPlayers();

            return newViewport;
        }

        private void ResizeViewports(Vector2D<int> obj)
        {
            using var sample = Engine.Profiler.Start("XRWindow.ResizeViewports");

            void SetSize(XRViewport vp)
            {
                vp.Resize((uint)obj.X, (uint)obj.Y, true);
                //vp.SetInternalResolution((int)(obj.X * 0.5f), (int)(obj.X * 0.5f), false);
                //vp.SetInternalResolutionPercentage(0.5f, 0.5f);
            }
            Viewports.ForEach(SetSize);
        }

        #endregion

        #region World Hierarchy Encoding (Networking)

        private WorldHierarchy? EncodeWorldHierarchy()
        {
            var world = TargetWorldInstance;
            if (world is null)
                return null;

            // Filter out editor-only nodes (nodes in the hidden editor scene)
            var rootNodes = world.RootNodes
                .Where(node => !world.IsInEditorScene(node))
                .Select(EncodeNode)
                .ToArray();
            return new WorldHierarchy
            {
                GameModeFullTypeDef = world.GameMode?.GetType().FullName,
                RootNodes = rootNodes,
            };
        }

        private NodeRepresentation EncodeNode(SceneNode node)
        {
            var transform = node.Transform;
            NodeRepresentation[] children = [.. transform.Children.Where(x => x.SceneNode is not null).Select(x => EncodeNode(x.SceneNode!))];
            (string FullTypeDef, Guid ServerGUID)[] components = [.. node.Components.Select(x => (x.GetType().FullName ?? string.Empty, x.ID))];
            return new NodeRepresentation
            {
                ServerGUID = node.ID,
                TransformType = (transform.GetType().FullName, transform.ID),
                ComponentTypes = components,
                Children = children,
            };
        }

        #endregion

        #region Disposal

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposing = true;
            try
            {
                // Ensure engine tick callbacks are detached before any other teardown.
                if (IsTickLinked)
                {
                    IsTickLinked = false;
                    try
                    {
                        EndTick();
                    }
                    catch
                    {
                        // Best-effort cleanup; shutdown paths should not throw.
                    }
                }
                else if (_rendererInitialized)
                {
                    // Defensive: if renderer was initialized without tick linking, still attempt cleanup.
                    try
                    {
                        Renderer.WaitForGpu();
                        Renderer.DestroyCachedAPIRenderObjects();
                        Engine.Rendering.DestroyObjectsForRenderer(Renderer);
                        Renderer.CleanUp();
                    }
                    catch
                    {
                    }

                    _rendererInitialized = false;
                }

                // Free dockable scene-panel GPU resources.
                _scenePanelAdapter.Dispose();

                // Unhook input.
                if (Input is not null)
                {
                    try
                    {
                        Input.ConnectionChanged -= Input_ConnectionChanged;
                    }
                    catch
                    {
                    }

                    try
                    {
                        (Input as IDisposable)?.Dispose();
                    }
                    catch
                    {
                    }

                    Input = null;
                }

                // Unhook window events and release viewports.
                UnlinkWindow();
                try
                {
                    Viewports.Clear();
                }
                catch
                {
                }

                // Finally, release the window itself if possible.
                try
                {
                    (Window as IDisposable)?.Dispose();
                }
                catch
                {
                }
            }
            finally
            {
                _isDisposed = true;
                _isDisposing = false;
                GC.SuppressFinalize(this);
            }
        }

        #endregion
    }
}

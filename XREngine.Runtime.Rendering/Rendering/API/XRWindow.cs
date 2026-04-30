using XREngine.Extensions;
using Newtonsoft.Json;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using System.Threading;
using System.Threading.Tasks;
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
    public sealed class XRWindow : XRBase, IRuntimeRenderWindowHost, IDisposable
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

        private sealed class WindowInitializationProbe : IDisposable
        {
            public string Title { get; }
            public ContextAPI API { get; }
            public bool UseNativeTitleBar { get; }
            public long StartTimestamp { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
            public CancellationTokenSource CancellationSource { get; } = new();
            public int Stage;

            public WindowInitializationProbe(string title, ContextAPI api, bool useNativeTitleBar)
            {
                Title = title;
                API = api;
                UseNativeTitleBar = useNativeTitleBar;
            }

            public TimeSpan Elapsed
                => System.Diagnostics.Stopwatch.GetElapsedTime(StartTimestamp);

            public void Dispose()
            {
                CancellationSource.Cancel();
                CancellationSource.Dispose();
            }
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
        private IRuntimeRenderWorld? _targetWorldInstance;
        private bool _isFocused = false;

        // Editor scene-panel presentation (dockable viewport panel) adapter.
        private readonly IRuntimeWindowScenePanelAdapter _scenePanelAdapter;
        private WindowInitializationProbe? _windowInitializationProbe;

        #endregion

        private bool _isDisposing;
        private bool _isDisposed;
        private int _pendingCloseRequested;

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

        public IRuntimeRenderWorld? TargetWorldInstance
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

        /// <summary>
        /// Per-window request to keep VSync enabled even when the global engine policy is off.
        /// </summary>
        public bool WindowVSyncRequested { get; }

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

        public void ApplyVSyncMode(EVSyncMode globalVSyncMode)
        {
            if (_isDisposed || _isDisposing)
                return;

            if (Engine.IsRenderThread)
                ApplyVSyncModeOnRenderThread(globalVSyncMode);
            else
                Engine.EnqueueRenderThreadTask(
                    () => ApplyVSyncModeOnRenderThread(globalVSyncMode),
                    $"XRWindow.ApplyVSync[{GetHashCode()}]");
        }

        public void RequestClose()
        {
            if (_isDisposed || _isDisposing)
                return;

            if (Engine.IsRenderThread)
            {
                RequestCloseOnRenderThread();
                return;
            }

            Engine.EnqueueRenderThreadTask(
                RequestCloseOnRenderThread,
                $"Viewport.CloseWindow[{GetHashCode()}]");
        }

        private void ApplyVSyncModeOnRenderThread(EVSyncMode globalVSyncMode)
        {
            if (_isDisposed || _isDisposing)
                return;

            bool enableVSync = WindowVSyncRequested || globalVSyncMode != EVSyncMode.Off;
            bool isOpenGlWindow = Window.API.API == ContextAPI.OpenGL;

            try
            {
                if (isOpenGlWindow)
                    Window.MakeCurrent();

                Window.VSync = enableVSync;

                if (!isOpenGlWindow || !enableVSync || globalVSyncMode != EVSyncMode.Adaptive)
                    return;

                try
                {
                    Window.GLContext?.SwapInterval(-1);
                }
                catch (Exception adaptiveEx)
                {
                    Debug.RenderingWarningEvery(
                        $"XRWindow.AdaptiveVSync[{GetHashCode()}]",
                        TimeSpan.FromSeconds(5),
                        "[XRWindow] Adaptive VSync is unavailable for window {0}; falling back to standard VSync. {1}",
                        GetHashCode(),
                        adaptiveEx.Message);

                    Window.GLContext?.SwapInterval(1);
                }
            }
            catch (Exception ex)
            {
                Debug.RenderingWarningEvery(
                    $"XRWindow.ApplyVSync[{GetHashCode()}]",
                    TimeSpan.FromSeconds(5),
                    "[XRWindow] Failed to apply VSync policy for window {0}. {1}",
                    GetHashCode(),
                    ex.Message);
            }
        }

        private void RequestCloseOnRenderThread()
        {
            if (_isDisposed || _isDisposing)
                return;

            if (Engine.IsDispatchingRenderFrame)
            {
                Interlocked.Exchange(ref _pendingCloseRequested, 1);
                return;
            }

            PerformCloseRequest();
        }

        private void ProcessDeferredCloseRequest()
        {
            if (Interlocked.Exchange(ref _pendingCloseRequested, 0) == 0)
                return;

            PerformCloseRequest();
        }

        private void PerformCloseRequest()
        {
            try
            {
                Window.Close();
            }
            catch
            {
            }
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

        public XRWindow(WindowOptions options, bool useNativeTitleBar, bool windowVSyncRequested = false)
        {
            _viewports.CollectionChanged += ViewportsChanged;
            _scenePanelAdapter = RuntimeRenderingHostServices.Current.CreateWindowScenePanelAdapter();

            Debug.Out(
                "[XRWindow] Constructing window title='{0}' size={1}x{2} pos=({3},{4}) api={5}",
                options.Title,
                options.Size.X,
                options.Size.Y,
                options.Position.X,
                options.Position.Y,
                options.API.API);

            Silk.NET.Windowing.Window.PrioritizeGlfw();
            Window = Silk.NET.Windowing.Window.Create(options);
            UseNativeTitleBar = useNativeTitleBar;
            WindowVSyncRequested = windowVSyncRequested;
            _windowInitializationProbe = new WindowInitializationProbe(options.Title ?? string.Empty, options.API.API, useNativeTitleBar);
            StartWindowInitializationWatchdog(_windowInitializationProbe);

            LinkWindow();
            Debug.Out("[XRWindow] Calling Window.Initialize for hash={0}.", GetHashCode());
            try
            {
                Window.Initialize();
            }
            finally
            {
                _windowInitializationProbe?.Dispose();
                _windowInitializationProbe = null;
            }

            // GLFW does not reliably honor the position hint supplied via WindowOptions
            // on Windows, so we force the requested position after initialization.
            if (Window.Position != options.Position)
            {
                Debug.Out(
                    "[XRWindow] Forcing position from ({0},{1}) to requested ({2},{3}) for hash={4}.",
                    Window.Position.X,
                    Window.Position.Y,
                    options.Position.X,
                    options.Position.Y,
                    GetHashCode());
                Window.Position = options.Position;
            }

            Debug.Out(
                "[XRWindow] Window.Initialize completed for hash={0}. Framebuffer={1}x{2}",
                GetHashCode(),
                Window.FramebufferSize.X,
                Window.FramebufferSize.Y);

            Renderer = (AbstractRenderer)RuntimeRenderingHostServices.Current.CreateRenderer(this, ToRuntimeGraphicsApiKind(Window.API.API));
            Debug.Out("[XRWindow] Renderer created for hash={0}. RendererType={1}", GetHashCode(), Renderer.GetType().Name);
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
                    RuntimeRenderingHostServices.Current.ReplicateWindowTargetWorldChange(this);
                    break;
            }
        }

        #endregion

        #region Public Methods - World Management

        public void SetWorld(object? targetWorld)
        {
            if (targetWorld is IRuntimeRenderWorld worldInstance)
                TargetWorldInstance = worldInstance;
        }

        #endregion

        #region Public Methods - Viewport Management

        public XRViewport GetOrAddViewportForPlayer(IPawnController controller, bool autoSizeAllViewports)
            => (controller.Viewport as XRViewport) ?? AddViewportForPlayer(controller, autoSizeAllViewports);

        /// <summary>
        /// Remakes all viewports in order of active local player indices.
        /// </summary>
        public void ResizeAllViewportsAccordingToPlayers()
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.ResizeAllViewportsAccordingToPlayers");

            IPawnController[] players = [.. Viewports
                .Select(x => x.AssociatedPlayer)
                .OfType<IPawnController>()
                .Where(x => x.IsLocal)
                .Distinct()
                .OrderBy(x => (int)(x.LocalPlayerIndex ?? 0))];
            foreach (var viewport in Viewports)
                viewport.Destroy();
            Viewports.Clear();
            for (int i = 0; i < players.Length; i++)
                AddViewportForPlayer(players[i], false);
        }

        public void UpdateViewportSizes()
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.UpdateViewportSizes");
            ResizeViewports(Window.Size);
        }

        #endregion

        #region Public Methods - Player Registration

        public void RegisterLocalPlayer(ELocalPlayerIndex playerIndex, bool autoSizeAllViewports)
            => RegisterController(Engine.State.GetOrCreateLocalPlayer(playerIndex), autoSizeAllViewports);

        public void RegisterController(IPawnController controller, bool autoSizeAllViewports)
            => GetOrAddViewportForPlayer(controller, autoSizeAllViewports).AssociatedPlayer = controller;

        /// <summary>
        /// Ensures the given controller is registered with this window and has a valid viewport.
        /// This is more defensive than <see cref="RegisterController"/> and is intended for
        /// scenarios like snapshot restore where runtime-only references (controller.Viewport,
        /// viewport.AssociatedPlayer) can become stale or inconsistent.
        /// </summary>
        public XRViewport? EnsureControllerRegistered(IPawnController controller, bool autoSizeAllViewports)
        {
            if (controller is null)
                return null;

            // If the controller is holding a stale viewport reference (not owned by this window), drop it.
            if (controller.Viewport is XRViewport existingVP && !Viewports.Contains(existingVP))
                controller.Viewport = null;

            // Prefer an existing viewport already tied to the same local player index.
            var existingByIndex = Viewports.FirstOrDefault(vp => vp.AssociatedPlayer?.LocalPlayerIndex == controller.LocalPlayerIndex);
            if (existingByIndex is not null)
            {
                existingByIndex.AssociatedPlayer = controller;
                return existingByIndex;
            }

            // Otherwise, reuse an unassigned viewport if one exists.
            var unassigned = Viewports.FirstOrDefault(vp => vp.AssociatedPlayer is null);
            if (unassigned is not null)
            {
                unassigned.AssociatedPlayer = controller;
                return unassigned;
            }

            // Fallback: create a viewport for the controller.
            RegisterController(controller, autoSizeAllViewports);
            return controller.Viewport as XRViewport;
        }

        public void UnregisterLocalPlayer(ELocalPlayerIndex playerIndex)
        {
            IPawnController? controller = Engine.State.GetLocalPlayer(playerIndex);
            if (controller is not null)
                UnregisterController(controller);
        }

        public void UnregisterController(IPawnController controller)
        {
            if (controller.Viewport is XRViewport vp && Viewports.Contains(vp))
                controller.Viewport = null;
        }

        #endregion

        #region Public Methods - Rendering

        public void RenderViewports()
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.RenderViewports");
            foreach (var viewport in Viewports)
            {
                using var viewportSample = RuntimeRenderingHostServices.Current.StartProfileScope($"XRViewport.Render[{viewport.Index}]");
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

            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.RenderViewportsToFBO");
            foreach (var viewport in Viewports)
            {
                using var viewportSample = RuntimeRenderingHostServices.Current.StartProfileScope($"XRViewport.RenderToFBO[{viewport.Index}]");
                viewport.Render(targetFBO);
            }
        }

        #endregion

        #region Window Event Handlers

        private void Window_Load()
        {
            if (_windowInitializationProbe is not null)
                Volatile.Write(ref _windowInitializationProbe.Stage, 1);

            Debug.Out("[XRWindow] Load event for hash={0}.", GetHashCode());

            //Task.Run(() =>
            //{
                Input = Window.CreateInput();
                Input.ConnectionChanged += Input_ConnectionChanged;
            //});

            if (_windowInitializationProbe is not null)
                Volatile.Write(ref _windowInitializationProbe.Stage, 2);

            Debug.Out("[XRWindow] Input created for hash={0}.", GetHashCode());
        }

        private void StartWindowInitializationWatchdog(WindowInitializationProbe probe)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), probe.CancellationSource.Token);

                    while (!probe.CancellationSource.IsCancellationRequested)
                    {
                        Debug.LogWarning($"[XRWindow] Window.Initialize still running after {probe.Elapsed.TotalMilliseconds:F0} ms. stage={DescribeWindowInitializationStage(Volatile.Read(ref probe.Stage))} title='{probe.Title}' api={probe.API} nativeTitleBar={probe.UseNativeTitleBar}");

                        await Task.Delay(TimeSpan.FromSeconds(5), probe.CancellationSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[XRWindow] Window init watchdog failed: {ex.Message}");
                }
            });
        }

        private static string DescribeWindowInitializationStage(int stage)
            => stage switch
            {
                0 => "before-load-event",
                1 => "load-event-before-input",
                2 => "load-event-after-input",
                _ => $"unknown-{stage}",
            };

        private void Window_Closing()
        {
            if (!_isDisposing && !_isDisposed)
            {
                bool allowClose = RuntimeRenderingHostServices.Current.AllowWindowClose(this);
                if (!allowClose)
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
                RuntimeRenderingHostServices.Current.RemoveWindow(this);
            }
        }

        private bool TryCancelCloseRequest()
        {
            if (Window is null)
                return false;

            // IWindow.IsClosing has a setter via explicit interface implementation.
            // The concrete GlfwWindow type inherits a read-only IsClosing from
            // ViewImplementationBase, so reflection on the runtime type fails.
            // Use the interface property directly — Window is already IWindow.
            try
            {
                Window.IsClosing = false;
                return true;
            }
            catch
            {
                // ignored — fall through to reflection fallbacks
            }

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
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.FramebufferResize");

            //Debug.Out("Window resized to {0}x{1}", obj.X, obj.Y);
            Viewports.ForEach(vp => vp.Resize((uint)obj.X, (uint)obj.Y, true));

            _scenePanelAdapter.OnFramebufferResized(this, obj.X, obj.Y);

            Renderer.FrameBufferInvalidated();

            // Clear any circuit breaker backoff so the next frame renders immediately
            // with freshly recreated resources instead of waiting out old failures.
            ResetRenderCircuitBreaker();

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
            RuntimeRenderingHostServices.Current.SubscribePlayModeTransitions(OnPlayModeTransition);
            Engine.PlayMode.PostEnterPlay += OnPlayModeTransition;
            Engine.PlayMode.PreExitPlay += OnPlayModeTransition;
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
            RuntimeRenderingHostServices.Current.UnsubscribePlayModeTransitions(OnPlayModeTransition);
            Engine.PlayMode.PostEnterPlay -= OnPlayModeTransition;
            Engine.PlayMode.PreExitPlay -= OnPlayModeTransition;
        }

        private void OnPlayModeTransition()
        {
            bool isTransitioning = Engine.PlayMode.IsTransitioning;
            Debug.Out($"[XRWindow] OnPlayModeTransition called. PlayModeState={Engine.PlayMode.State} Viewports={Viewports.Count} Transitioning={isTransitioning}");
            
            // Invalidate scene panel resources IMMEDIATELY so stale textures don't persist.
            // Using immediate destruction ensures the GL texture handle is invalidated before
            // ImGui tries to display it on the next frame.
            _scenePanelAdapter.InvalidateResourcesImmediate();

            // Invalidate viewport render pipeline resources so stale textures/FBOs
            // from the previous play mode state don't persist into the new state.
            // Use InvalidatePhysicalResources (retains descriptor metadata) instead of
            // DestroyCache so that render commands can lazily recreate FBOs/textures
            // on the next frame without losing registry structure.
            foreach (var viewport in Viewports)
            {
                Debug.Out($"[XRWindow] Invalidating pipeline resources for VP[{viewport.Index}] CameraComponent={viewport.CameraComponent?.Name ?? "<null>"} ActiveCamera={viewport.ActiveCamera?.GetHashCode().ToString() ?? "null"}");
                viewport.RenderPipelineInstance.InvalidatePhysicalResources();
            }

            if (isTransitioning)
                Debug.Out("[XRWindow] Play-mode transition is active; viewport pipelines are torn down until the state stabilizes.");
            else
                Debug.Out("[XRWindow] Play-mode transition settled; viewport pipelines will rebuild from retained descriptors on the next stable frame.");

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
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.VerifyTick");

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
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.BeginTick");

            Debug.Out("[XRWindow] BeginTick hash={0} viewports={1} targetWorld={2}", GetHashCode(), Viewports.Count, TargetWorldInstance?.TargetWorldName ?? "<null>");

            Renderer.Initialize();
            _rendererInitialized = true;
            RuntimeRenderingHostServices.Current.SubscribeWindowTickCallbacks(SwapBuffers, RenderFrame);

            Debug.Out("[XRWindow] Tick callbacks subscribed hash={0}.", GetHashCode());
        }

        private void EndTick()
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.EndTick");

            using (RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.EndTick.Unsubscribe"))
                RuntimeRenderingHostServices.Current.UnsubscribeWindowTickCallbacks(SwapBuffers, RenderFrame);
            using (RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.EndTick.WaitForGpu"))
                Renderer.WaitForGpu();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.EndTick.DestroyCachedAPIRenderObjects"))
                Renderer.DestroyCachedAPIRenderObjects();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.EndTick.DestroyObjectsForRenderer"))
                RuntimeRenderingHostServices.Current.DestroyObjectsForRenderer(Renderer);
            using (RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.EndTick.RendererCleanUp"))
                Renderer.CleanUp();
            _rendererInitialized = false;
            using (RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.EndTick.DoEvents"))
                Window.DoEvents();
        }

        private void SwapBuffers()
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.SwapBuffers");
        }

        private void RenderFrame()
        {
            // Guard against rendering after window is disposed or GL context is invalid
            if (_isDisposed || _isDisposing)
                return;

            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.Timer.RenderFrame");

            {
                using var eventsSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.Timer.DoEvents");
                Window.DoEvents();
            }

            // Re-check after DoEvents in case window was closed
            if (_isDisposed || _isDisposing)
                return;

            {
                using var doRenderSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.Timer.DoRender");
                Window.DoRender();
            }

            if (_isDisposed || _isDisposing)
                return;

            using (var mainThreadJobsSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.Timer.PostRenderMainThreadJobs"))
            {
                // Draw the frame first, then spend a small budget on queued GPU work.
                // This keeps texture uploads and property updates from delaying visible rendering.
                Engine.ProcessMainThreadTasks();
            }

            if (_isDisposed || _isDisposing)
                return;

            // Window.Close must not run inside the active DoRender callback or the render-thread
            // job pump for the current frame; defer it until the frame boundary is complete.
            ProcessDeferredCloseRequest();
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

            using var frameSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.RenderFrame");

            // Reset per-frame rendering statistics at the start of each frame
            RuntimeRenderingHostServices.Current.BeginRenderStatsFrame();

            // Process any pending async buffer uploads within the frame budget
            using (var uploadSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.ProcessPendingUploads"))
            {
                Renderer.ProcessPendingUploads();
            }

            try
            {
                Renderer.Active = true;
                AbstractRenderer.Current = Renderer;

                bool useScenePanelMode = RuntimeRenderingHostServices.Current.IsWindowScenePanelPresentationEnabled;
                bool forceFullViewport = RuntimeRenderingHostServices.Current.ForceFullViewport;
                if (forceFullViewport)
                    useScenePanelMode = false;
                bool mirrorByComposition =
                    RuntimeRenderingHostServices.Current.IsInVR &&
                    RuntimeRenderingHostServices.Current.IsOpenXRActive &&
                    RuntimeRenderingHostServices.Current.RenderWindowsWhileInVR &&
                    RuntimeRenderingHostServices.Current.VrMirrorComposeFromEyeTextures;
                bool canRenderWindowViewports =
                    !RuntimeRenderingHostServices.Current.IsInVR ||
                    (RuntimeRenderingHostServices.Current.RenderWindowsWhileInVR && !mirrorByComposition);

                //LogRenderDiagnostics(delta, useScenePanelMode, canRenderWindowViewports, forceFullViewport);
                ApplyForcedDebugOpaquePipelineOverride();

                using (var preRenderSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.GlobalPreRender"))
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

                using (var renderCallbackSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.RenderViewportsCallback"))
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
                    if (Engine.PlayMode.IsTransitioning)
                    {
                        Debug.RenderingEvery(
                            $"XRWindow.RenderCallback.TransitionSuspended.{GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[RenderDiag] Window viewport rendering suspended during play-mode transition. Window={0} State={1} Viewports={2}",
                            GetHashCode(),
                            Engine.PlayMode.State,
                            Viewports.Count);
                    }
                    else
                    {
                        RenderWindowViewports(useScenePanelMode, canRenderWindowViewports, mirrorByComposition);
                    }
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

                using (var postRenderSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.GlobalPostRender"))
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

                if (Engine.StartupPresentationEnabled)
                {
                    using var startupPresentationSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.StartupPresentationMarker");
                    var fullRegion = new BoundingRectangle(0, 0, Window.FramebufferSize.X, Window.FramebufferSize.Y);
                    int markerWidth = Math.Min(96, Window.FramebufferSize.X);
                    int markerHeight = Math.Min(96, Window.FramebufferSize.Y);
                    var markerRegion = new BoundingRectangle(0, 0, markerWidth, markerHeight);

                    Renderer.BindFrameBuffer(EFramebufferTarget.Framebuffer, null);
                    Renderer.SetRenderArea(fullRegion);
                    Renderer.SetCroppingEnabled(true);
                    Renderer.CropRenderArea(markerRegion);
                    Renderer.ClearColor(Engine.StartupPresentationClearColor);
                    Renderer.Clear(color: true, depth: false, stencil: false);
                    Renderer.SetCroppingEnabled(false);
                    Renderer.SetRenderArea(fullRegion);
                }

                // Allow the renderer to perform any per-window end-of-frame work (e.g., Vulkan acquire/submit/present).
                // This MUST run even when viewport rendering fails, otherwise Vulkan never presents and the window
                // shows uninitialized (white) content. With empty/partial frame ops, the Vulkan backend will at
                // minimum clear to the background color and render the debug triangle + ImGui overlay.
                Renderer.RenderWindow(delta);

                using (var postViewportsSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.PostRenderViewportsCallback"))
                {
                    PostRenderViewportsCallback?.Invoke();
                }

                // Tick render-thread coroutines (e.g. progressive texture uploads) while the renderer
                // is still marked active so that IsRendererActive guards inside those coroutines pass.
                using (var inFrameJobsSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.InFrameMainThreadJobs"))
                {
                    Engine.ProcessMainThreadTasks();
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
            bool forceDebugOpaque = RuntimeRenderingHostServices.Current.ShouldForceDebugOpaquePipeline;
            if (!forceDebugOpaque)
                return;

            foreach (var viewport in Viewports)
            {
                if (viewport.RenderPipeline is DebugOpaqueRenderPipeline)
                    continue;

                viewport.RenderPipeline = RuntimeRenderingHostServices.Current.CreateDebugOpaquePipelineOverride() as RenderPipeline;
                if (viewport.RenderPipeline is null)
                    continue;
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

            Debug.RenderingEvery(
                keyBase + ".Mode",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] Window mode: PanelMode={0} ForcedFull={1} Pref={2} CanRender={3} Viewports={4} TargetWorld={5} PlayState={6} Delta={7:F4} DrawCalls={8} VkReq={9} VkCull={10} VkEmit={11} VkConsume={12} GpuVisible(O/M/A/E)={13}/{14}/{15}/{16}",
                useScenePanelMode,
                forceFullViewport,
                Engine.EditorPreferences.ViewportPresentationMode,
                canRenderWindowViewports,
                Viewports.Count,
                TargetWorldInstance?.TargetWorldName ?? "<null>",
                TargetWorldInstance?.IsPlaySessionActive.ToString() ?? "<null>",
                delta,
                Engine.Rendering.Stats.DrawCalls,
                Engine.Rendering.Stats.VulkanRequestedDraws,
                Engine.Rendering.Stats.VulkanCulledDraws,
                Engine.Rendering.Stats.VulkanEmittedIndirectDraws,
                Engine.Rendering.Stats.VulkanConsumedDraws,
                Engine.Rendering.Stats.GpuTransparencyOpaqueOrOtherVisible,
                Engine.Rendering.Stats.GpuTransparencyMaskedVisible,
                Engine.Rendering.Stats.GpuTransparencyApproximateVisible,
                Engine.Rendering.Stats.GpuTransparencyExactVisible);

            if (!canRenderWindowViewports)
            {
                Debug.RenderingEvery(
                    keyBase + ".VRGated",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Window gated by VR. IsInVR={0}, RenderWindowsWhileInVR={1}",
                    RuntimeRenderingHostServices.Current.IsInVR,
                    RuntimeRenderingHostServices.Current.RenderWindowsWhileInVR);
            }

            if (!ShouldBeRendering())
            {
                Debug.RenderingWarningEvery(
                    keyBase + ".NotRendering",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Window not rendering: Viewports={0}, TargetWorldInstanceNull={1}, PresentationMode={2}, CanRenderWindowViewports={3}",
                    Viewports.Count,
                    TargetWorldInstance is null,
                    RuntimeRenderingHostServices.Current.IsWindowScenePanelPresentationEnabled,
                    canRenderWindowViewports);
            }

            foreach (var vp in Viewports)
            {
                var activeCamera = vp.ActiveCamera;
                var world = vp.World;
                bool hasPipeline = vp.RenderPipelineInstance.Pipeline is not null;
                int renderCommandCount = vp.RenderPipelineInstance.MeshRenderCommands.GetRenderingCommandCount();
                int updatingCommandCount = vp.RenderPipelineInstance.MeshRenderCommands.GetUpdatingCommandCount();
                int rootNodeCount = world?.RootNodes.Count ?? 0;
                int directionalLightCount = world?.Lights.DynamicDirectionalLights.Count ?? 0;
                int pointLightCount = world?.Lights.DynamicPointLights.Count ?? 0;
                int spotLightCount = world?.Lights.DynamicSpotLights.Count ?? 0;
                string pipelineName = vp.RenderPipelineInstance.Pipeline?.GetType().Name ?? "<null>";
                string cameraPosition = activeCamera?.Transform is not null
                    ? activeCamera.Transform.WorldTranslation.ToString()
                    : "<null>";

                Debug.RenderingEvery(
                    keyBase + $".VP.{vp.Index}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] VP[{0}] Region={1}x{2}@({3},{4}) Internal={5}x{6} World={7} Play={8} RootNodes={9} ActiveCameraNull={10} CamPos={11} Pipeline={12} RenderCmds={13} UpdateCmds={14} Lights(D/P/S)={15}/{16}/{17} Suppress3D={18} AssocPlayer={19}",
                    vp.Index,
                    vp.Width,
                    vp.Height,
                    vp.X,
                    vp.Y,
                    vp.InternalWidth,
                    vp.InternalHeight,
                    world?.TargetWorldName ?? "<null>",
                    world?.IsPlaySessionActive.ToString() ?? "<null>",
                    rootNodeCount,
                    activeCamera is null,
                    cameraPosition,
                    pipelineName,
                    renderCommandCount,
                    updatingCommandCount,
                    directionalLightCount,
                    pointLightCount,
                    spotLightCount,
                    vp.Suppress3DSceneRendering,
                    vp.AssociatedPlayer?.LocalPlayerIndex.ToString() ?? "<none>");

                if (canRenderWindowViewports && hasPipeline && activeCamera is not null && world is not null && rootNodeCount > 0 && renderCommandCount == 0)
                {
                    Debug.RenderingWarningEvery(
                        keyBase + $".VP.{vp.Index}.NoRenderCommands",
                        TimeSpan.FromSeconds(1),
                        "[RenderDiag] VP[{0}] has world content but zero render commands. RootNodes={1} Lights(D/P/S)={2}/{3}/{4} Pipeline={5} Suppress3D={6}",
                        vp.Index,
                        rootNodeCount,
                        directionalLightCount,
                        pointLightCount,
                        spotLightCount,
                        pipelineName,
                        vp.Suppress3DSceneRendering);
                }
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
                    RuntimeRenderingHostServices.Current.TryRenderDesktopMirrorComposition(targetWidth, targetHeight);
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

        private XRViewport AddViewportForPlayer(IPawnController? controller, bool autoSizeAllViewports)
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.AddViewportForPlayer");

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
                controller is null ? "<null>" : $"P{(int)(controller.LocalPlayerIndex ?? 0) + 1}",
                (controller?.Viewport as XRViewport)?.GetHashCode() ?? 0);

            if (autoSizeAllViewports)
                ResizeAllViewportsAccordingToPlayers();

            return newViewport;
        }

        private void ResizeViewports(Vector2D<int> obj)
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.ResizeViewports");

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
                GameModeFullTypeDef = world.GameModeObject?.GetType().FullName,
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
                        RuntimeRenderingHostServices.Current.DestroyObjectsForRenderer(Renderer);
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
                Interlocked.Exchange(ref _pendingCloseRequested, 0);
                _isDisposed = true;
                _isDisposing = false;
                GC.SuppressFinalize(this);
            }
        }

        #endregion

        internal string? EncodeTargetWorldHierarchyJson()
            => JsonConvert.SerializeObject(EncodeWorldHierarchy());

        private static RuntimeGraphicsApiKind ToRuntimeGraphicsApiKind(ContextAPI api)
            => api switch
            {
                ContextAPI.OpenGL => RuntimeGraphicsApiKind.OpenGL,
                ContextAPI.Vulkan => RuntimeGraphicsApiKind.Vulkan,
                _ => RuntimeGraphicsApiKind.Unknown,
            };
    }
}

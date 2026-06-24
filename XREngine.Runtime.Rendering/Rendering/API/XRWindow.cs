using XREngine.Extensions;
using Newtonsoft.Json;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Input.Devices.Glfw;
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
        public event Action<XRWindow, string[]>? FileDropped;
        public event Action<XRWindow>? ClosingRequested;
        public event Action<XRWindow, Vector2D<int>>? FramebufferResized;
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
        private bool _approvedNativeCloseInProgress;
        private int _pendingCloseRequested;
        private int _pendingFramebufferResize;
        private int _pendingFramebufferResizeWidth;
        private int _pendingFramebufferResizeHeight;
        private int _pendingInteractivePresentationResize;
        private int _pendingInteractivePresentationFramebufferWidth;
        private int _pendingInteractivePresentationFramebufferHeight;
        private int _effectiveFramebufferWidth;
        private int _effectiveFramebufferHeight;
        private int _effectiveWindowWidth;
        private int _effectiveWindowHeight;
        private int _interactiveResizeInProgress;
        private int _interactiveResizeRenderActive;
        private int _interactiveResizeRenderQueued;
        private int _normalRenderActive;
        private int _externalNativeEventPumpActive;
        private int _externalPumpDisposeStarted;
        private IInteractiveResizeStrategy _interactiveResizeStrategy;
        private readonly int _nativeWindowThreadId;
        private int _renderOwnerThreadId;
        private long _windowSurfaceSnapshotSequence;
        private long _windowEventSnapshotSequence;
        private readonly object _windowEventSnapshotSync = new();
        private WindowEventSnapshot _latestWindowEventSnapshot;
        private readonly WindowInputSnapshotAccumulator _inputSnapshotAccumulator = new();
        private readonly WindowResizeController _resizeController = new();
        private readonly HashSet<IKeyboard> _inputSnapshotKeyboards = [];
        private readonly HashSet<IMouse> _inputSnapshotMice = [];
        private long _pendingFullInternalResizeGeneration;
        private RuntimeWindowBackendKind _windowBackendKind = RuntimeWindowBackendKind.Unknown;
        private RuntimeWindowBackendOwnershipInfo _windowBackendOwnership =
            RuntimeWindowBackendOwnershipInfo.ForBackend(RuntimeWindowBackendKind.Unknown);

        private Exception? _lastRenderException;
        private int _consecutiveRenderFailures;
        private DateTime _renderDisabledUntilUtc;
        private bool _renderPermanentlyDisabled;
        private string? _renderPermanentlyDisabledReason;

        private bool _rendererInitialized;
        private AbstractRenderer _renderer = null!;
        private bool _rendererRecreationInProgress;
        private int _rendererRecreationAttempts;
        private const int MaxRendererRecreationAttempts = 3;

        #region Properties

        /// <summary>
        /// Thread-affined Silk.NET window instance. Prefer snapshots, mailbox
        /// helpers, or explicit XRWindow wrapper APIs outside backend-owned code.
        /// </summary>
        public IWindow ThreadAffinedNativeWindow { get; }

        /// <summary>
        /// Compatibility escape hatch for backend-owned code while ownership
        /// migration continues. Prefer <see cref="ThreadAffinedNativeWindow"/>
        /// when raw access is unavoidable and named at the call site.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IWindow Window => ThreadAffinedNativeWindow;

        /// <summary>
        /// Interface to render a scene for this window using the requested graphics API.
        /// </summary>
        public AbstractRenderer Renderer => _renderer;

        /// <summary>
        /// Thread-affined Silk.NET input context. Gameplay and editor code should
        /// consume <see cref="LatestWindowInputSnapshot"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
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

        public EInteractiveWindowResizeStrategy InteractiveResizeStrategy { get; private set; }

        public InteractiveResizeDiagnostics InteractiveResizeDiagnostics { get; } = new();

        public string ActualWindowingBackendName => ResolveActualWindowingBackendName();

        public string WindowTitle => Window?.Title ?? string.Empty;

        public int NativeWindowThreadId => _nativeWindowThreadId;

        public int RenderOwnerThreadId => Volatile.Read(ref _renderOwnerThreadId);

        public RuntimeWindowBackendKind WindowBackendKind => _windowBackendKind;

        public RuntimeWindowBackendOwnershipInfo WindowBackendOwnership => _windowBackendOwnership;

        public WindowSurfaceSnapshot LatestWindowSurfaceSnapshot => _resizeController.LatestNativeSnapshot;

        public WindowEventSnapshot LatestWindowEventSnapshot
        {
            get
            {
                lock (_windowEventSnapshotSync)
                    return _latestWindowEventSnapshot;
            }
        }

        public WindowInputSnapshot LatestWindowInputSnapshot
        {
            get
            {
                return _inputSnapshotAccumulator.Latest;
            }
        }

        public WindowResizeExtents ResizeExtents => _resizeController.Extents;

        public bool IsNativeEventPumpExternallyOwned
            => Volatile.Read(ref _externalNativeEventPumpActive) != 0;

        public bool IsInteractiveResizeInProgress => Volatile.Read(ref _interactiveResizeInProgress) != 0;

        public Vector2D<int> EffectiveFramebufferSize
        {
            get
            {
                int width = Volatile.Read(ref _effectiveFramebufferWidth);
                int height = Volatile.Read(ref _effectiveFramebufferHeight);
                if (width > 0 && height > 0)
                    return new Vector2D<int>(width, height);

                return GetCurrentFramebufferSize();
            }
        }

        public Vector2D<int> EffectiveWindowSize
        {
            get
            {
                int width = Volatile.Read(ref _effectiveWindowWidth);
                int height = Volatile.Read(ref _effectiveWindowHeight);
                if (width > 0 && height > 0)
                    return new Vector2D<int>(width, height);

                return GetCurrentWindowSize();
            }
        }

        public Vector2D<int> WindowSizeSnapshot => EffectiveWindowSize;

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
        public bool IsDisposing => _isDisposing;
        internal bool IsStartupAttachmentComplete
            => !_isDisposed &&
               !_isDisposing &&
               Window is not null &&
               _renderer is not null &&
               EffectiveFramebufferSize.X > 0 &&
               EffectiveFramebufferSize.Y > 0 &&
               RenderOwnerThreadId != 0;

        public Exception? LastRenderException => _lastRenderException;

        public int ConsecutiveRenderFailures => _consecutiveRenderFailures;

        public DateTime? RenderDisabledUntilUtc
            => _renderDisabledUntilUtc == default ? null : _renderDisabledUntilUtc;

        public bool IsRenderTemporarilyDisabled
            => !_renderPermanentlyDisabled &&
               RenderDisabledUntilUtc is DateTime until &&
               DateTime.UtcNow < until;

        public bool IsRenderPermanentlyDisabled => _renderPermanentlyDisabled;

        public string? RenderPermanentlyDisabledReason => _renderPermanentlyDisabledReason;

        public TimeSpan? RenderDisableRemaining
        {
            get
            {
                if (_renderPermanentlyDisabled)
                    return null;

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

        private void DisableRenderingPermanently(string reason, Exception? exception = null)
        {
            if (_renderPermanentlyDisabled)
                return;

            _renderPermanentlyDisabled = true;
            _renderPermanentlyDisabledReason = reason;
            _renderDisabledUntilUtc = DateTime.MaxValue;
            _lastRenderException = exception;

            Debug.RenderingWarning(
                "[RenderDiag] Rendering permanently disabled for window {0}. Reason={1}{2}",
                GetHashCode(),
                reason,
                exception is null ? string.Empty : $" Exception={exception}");
        }

        public void ApplyVSyncMode(EVSyncMode globalVSyncMode)
        {
            if (_isDisposed || _isDisposing)
                return;

            if (RuntimeEngine.IsRenderThread)
                ApplyVSyncModeOnRenderThread(globalVSyncMode);
            else
                RuntimeEngine.EnqueueRenderThreadTask(
                    () => ApplyVSyncModeOnRenderThread(globalVSyncMode),
                    $"XRWindow.ApplyVSync[{GetHashCode()}]",
                    RenderThreadJobKind.RequiresGraphicsContext);
        }

        public void RequestClose()
        {
            if (_isDisposed || _isDisposing)
                return;

            if (IsNativeEventPumpExternallyOwned)
            {
                RuntimeRenderingHostServices.Current.EnqueueWindowThreadTask(
                    this,
                    RequestCloseOnWindowThread,
                    $"Viewport.CloseWindow.WindowThread[{GetHashCode()}]");
                return;
            }

            if (RuntimeEngine.IsRenderThread)
            {
                RequestCloseOnRenderThread();
                return;
            }

            RuntimeEngine.EnqueueRenderThreadTask(
                RequestCloseOnRenderThread,
                $"Viewport.CloseWindow[{GetHashCode()}]",
                RenderThreadJobKind.RequiresGraphicsContext);
        }

        public void RequestMouseCapture(bool captured)
        {
            if (_isDisposed || _isDisposing)
                return;

            string reason = captured
                ? $"XRWindow.CaptureMouse[{GetHashCode()}]"
                : $"XRWindow.ReleaseMouse[{GetHashCode()}]";

            if (IsNativeEventPumpExternallyOwned)
            {
                RuntimeRenderingHostServices.Current.EnqueueWindowThreadTask(
                    this,
                    () => SetMouseCaptureOnWindowThread(captured),
                    reason);
                return;
            }

            if (RuntimeEngine.IsRenderThread)
            {
                SetMouseCaptureOnWindowThread(captured);
                return;
            }

            RuntimeEngine.EnqueueRenderThreadTask(
                () => SetMouseCaptureOnWindowThread(captured),
                reason,
                RenderThreadJobKind.RequiresGraphicsContext);
        }

        private void SetMouseCaptureOnWindowThread(bool captured)
        {
            IInputContext? input = Input;
            if (input is null || input.Mice.Count == 0)
                return;

            input.Mice[0].Cursor.CursorMode = captured
                ? CursorMode.Disabled
                : CursorMode.Normal;
        }

        public void AttachExternalNativeEventPump(int pumpThreadId, string reason)
        {
            if (pumpThreadId != NativeWindowThreadId)
            {
                Debug.RenderingWarning(
                    "[WindowOwnership] Refusing external native event pump for window={0}. PumpThread={1} NativeWindowThread={2} Reason={3}.",
                    GetHashCode(),
                    pumpThreadId,
                    NativeWindowThreadId,
                    reason);
                return;
            }

            Interlocked.Exchange(ref _externalNativeEventPumpActive, 1);
            Debug.Rendering(
                "[WindowOwnership] External native event pump attached for window={0}. PumpThread={1} RenderOwnerThread={2} Backend={3} Reason={4}.",
                GetHashCode(),
                pumpThreadId,
                RenderOwnerThreadId,
                WindowBackendKind,
                reason);
        }

        public void PumpNativeWindowEventsFromHost()
        {
            if (_isDisposed || _isDisposing)
                return;

            WarnIfNotNativeWindowThread("Window.DoEvents.WindowPumpHost");

            using (RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.WindowPumpHost.DoEvents"))
                Window.DoEvents();

            if (_isDisposed || _isDisposing)
                return;

            Vector2D<int> framebufferSize = GetCurrentFramebufferSize();
            Vector2D<int> windowSize = GetCurrentWindowSize();
            UpdateEffectiveFramebufferSize(framebufferSize);
            UpdateEffectiveWindowSize(windowSize);
            PublishWindowSurfaceSnapshot(framebufferSize, windowSize, IsInteractiveResizeInProgress);
            PublishWindowEventSnapshot(closeRequested: false, closeApproved: false);
            PublishWindowInputSnapshot();
        }

        private void ApplyVSyncModeOnRenderThread(EVSyncMode globalVSyncMode)
        {
            if (_isDisposed || _isDisposing)
                return;

            WarnIfNotRenderOwnerThread("ApplyVSyncMode");

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

            if (RuntimeEngine.IsDispatchingRenderFrame)
            {
                Interlocked.Exchange(ref _pendingCloseRequested, 1);
                return;
            }

            PerformCloseRequest();
        }

        private void RequestCloseOnWindowThread()
        {
            if (_isDisposed || _isDisposing)
                return;

            WarnIfNotNativeWindowThread("Window.Close");
            PerformCloseRequest();
        }

        private void ProcessDeferredCloseRequest()
        {
            if (Interlocked.Exchange(ref _pendingCloseRequested, 0) == 0)
                return;

            if (IsNativeEventPumpExternallyOwned)
            {
                RuntimeRenderingHostServices.Current.EnqueueWindowThreadTask(
                    this,
                    RequestCloseOnWindowThread,
                    $"Viewport.CloseWindow.DeferredWindowThread[{GetHashCode()}]");
                return;
            }

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

            if (RuntimeEngine.IsRenderThread)
            {
                VerifyTick();
                return;
            }

            RuntimeEngine.EnqueueRenderThreadTask(
                VerifyTick,
                $"XRWindow.VerifyTick[{GetHashCode()}]",
                RenderThreadJobKind.RequiresGraphicsContext);
        }

        /// <summary>
        /// Destroys viewport-panel mode GPU resources so they are recreated on demand.
        /// Useful after swapchain/framebuffer invalidation or device/context transitions.
        /// </summary>
        public void InvalidateScenePanelResources()
        {
            if (_isDisposed || _isDisposing)
                return;

            if (RuntimeEngine.IsRenderThread)
            {
                _scenePanelAdapter.InvalidateResources();
                return;
            }

            RuntimeEngine.EnqueueRenderThreadTask(
                _scenePanelAdapter.InvalidateResources,
                $"XRWindow.InvalidateScenePanelResources[{GetHashCode()}]",
                RenderThreadJobKind.Framebuffer);
        }

        #region Constructor

        public XRWindow(
            WindowOptions options,
            bool useNativeTitleBar,
            bool windowVSyncRequested = false,
            EInteractiveWindowResizeStrategy interactiveResizeStrategy = EInteractiveWindowResizeStrategy.Default)
        {
            _viewports.CollectionChanged += ViewportsChanged;
            _scenePanelAdapter = RuntimeRenderingHostServices.Current.CreateWindowScenePanelAdapter();
            InteractiveResizeStrategy = interactiveResizeStrategy;
            _interactiveResizeStrategy = InteractiveResizeStrategyFactory.Create(interactiveResizeStrategy);
            _nativeWindowThreadId = Environment.CurrentManagedThreadId;
            Volatile.Write(ref _renderOwnerThreadId, _nativeWindowThreadId);

            Debug.Rendering(
                "[XRWindow] Constructing window title='{0}' size={1}x{2} pos=({3},{4}) api={5} interactiveResize={6} windowThread={7}",
                options.Title,
                options.Size.X,
                options.Size.Y,
                options.Position.X,
                options.Position.Y,
                options.API.API,
                interactiveResizeStrategy,
                _nativeWindowThreadId);

            ApplyWindowingBackendPreference(interactiveResizeStrategy);
            ThreadAffinedNativeWindow = Silk.NET.Windowing.Window.Create(options);
            UseNativeTitleBar = useNativeTitleBar;
            WindowVSyncRequested = windowVSyncRequested;
            _windowInitializationProbe = new WindowInitializationProbe(options.Title ?? string.Empty, options.API.API, useNativeTitleBar);
            StartWindowInitializationWatchdog(_windowInitializationProbe);

            LinkWindow();
            Debug.Rendering("[XRWindow] Calling Window.Initialize for hash={0}.", GetHashCode());
            try
            {
                Window.Initialize();
            }
            finally
            {
                _windowInitializationProbe?.Dispose();
                _windowInitializationProbe = null;
            }

            UpdateEffectiveFramebufferSize(GetCurrentFramebufferSize());
            UpdateEffectiveWindowSize(GetCurrentWindowSize());
            RefreshWindowBackendOwnership("post-initialize");
            PublishWindowSurfaceSnapshot(
                EffectiveFramebufferSize,
                EffectiveWindowSize,
                IsInteractiveResizeInProgress);
            PublishWindowEventSnapshot(closeRequested: false, closeApproved: false);
            PublishWindowInputSnapshot();
            RecordAllRenderExtents(EffectiveFramebufferSize);
            _interactiveResizeStrategy.Install(this);
            if (Input is not null)
                _interactiveResizeStrategy.OnInputCreated(Input);

            // GLFW does not reliably honor the position hint supplied via WindowOptions
            // on Windows, so we force the requested position after initialization.
            if (Window.Position != options.Position)
            {
                Debug.Rendering(
                    "[XRWindow] Forcing position from ({0},{1}) to requested ({2},{3}) for hash={4}.",
                    Window.Position.X,
                    Window.Position.Y,
                    options.Position.X,
                    options.Position.Y,
                    GetHashCode());
                Window.Position = options.Position;
            }

            Debug.Rendering(
                "[XRWindow] Window.Initialize completed for hash={0}. Framebuffer={1}x{2} Backend={3} InteractiveResize={4} WindowThread={5} RenderOwnerThread={6} Ownership={7}",
                GetHashCode(),
                EffectiveFramebufferSize.X,
                EffectiveFramebufferSize.Y,
                ActualWindowingBackendName,
                InteractiveResizeStrategy,
                NativeWindowThreadId,
                RenderOwnerThreadId,
                WindowBackendOwnership.Capabilities);

            _renderer = CreateRendererForCurrentWindow("initial window construction");
        }

        #endregion

        #region Interactive Resize

        private static void ApplyWindowingBackendPreference(EInteractiveWindowResizeStrategy strategy)
        {
            if (strategy == EInteractiveWindowResizeStrategy.SdlBackend)
            {
                Debug.Rendering("[InteractiveResize] Prioritizing Silk.NET SDL windowing backend.");
                Silk.NET.Windowing.Window.PrioritizeSdl();
                return;
            }

            Debug.Rendering("[InteractiveResize] Prioritizing Silk.NET GLFW windowing backend.");
            Silk.NET.Windowing.Window.PrioritizeGlfw();
        }

        public void SetInteractiveResizeStrategy(EInteractiveWindowResizeStrategy strategy)
        {
            if (_isDisposed || _isDisposing || strategy == InteractiveResizeStrategy)
                return;

            if (strategy == EInteractiveWindowResizeStrategy.SdlBackend ||
                InteractiveResizeStrategy == EInteractiveWindowResizeStrategy.SdlBackend)
            {
                Debug.RenderingWarning(
                    "[InteractiveResize] Runtime strategy change window={0} from={1} to={2}; actual backend remains {3} until the window is recreated.",
                    GetHashCode(),
                    InteractiveResizeStrategy,
                    strategy,
                    ActualWindowingBackendName);
            }

            try
            {
                _interactiveResizeStrategy.Uninstall();
            }
            catch (Exception ex)
            {
                Debug.RenderingWarning(
                    "[InteractiveResize] Strategy uninstall failed during runtime change window={0}. {1}",
                    GetHashCode(),
                    ex);
            }

            InteractiveResizeStrategy = strategy;
            _interactiveResizeStrategy = InteractiveResizeStrategyFactory.Create(strategy);
            _interactiveResizeStrategy.Install(this);
            if (Input is not null)
                _interactiveResizeStrategy.OnInputCreated(Input);

            Debug.Rendering(
                "[InteractiveResize] Runtime strategy changed window={0} strategy={1} backend={2}.",
                GetHashCode(),
                InteractiveResizeStrategy,
                ActualWindowingBackendName);
        }

        internal void QueueCurrentFramebufferResize(string reason)
            => QueueFramebufferResize(GetCurrentFramebufferSize(), reason);

        internal void QueueFramebufferResize(Vector2D<int> size, string reason)
            => QueueFramebufferResize(size, null, reason);

        internal void QueueFramebufferResize(Vector2D<int> size, Vector2D<int>? windowSize, string reason)
        {
            if (size.X <= 0 || size.Y <= 0)
                return;

            UpdateEffectiveFramebufferSize(size);
            if (windowSize.HasValue)
                UpdateEffectiveWindowSize(windowSize.Value);
            else
                UpdateEffectiveWindowSize(GetCurrentWindowSize());
            PublishWindowSurfaceSnapshot(size, EffectiveWindowSize, IsInteractiveResizeInProgress);

            QueueFullInternalResize(size, force: true, reason);
        }

        private void QueueFullInternalResize(Vector2D<int> size, bool force, string reason)
        {
            WindowResizeExtents extents = _resizeController.RequestFullInternalExtent(
                size,
                force,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                out bool requestAccepted);
            InteractiveResizeDiagnostics.RecordResizeExtents(extents);

            if (!requestAccepted)
                return;

            int currentPending = Volatile.Read(ref _pendingFramebufferResize);
            int currentWidth = Volatile.Read(ref _pendingFramebufferResizeWidth);
            int currentHeight = Volatile.Read(ref _pendingFramebufferResizeHeight);
            if (currentPending != 0 && currentWidth == size.X && currentHeight == size.Y)
                return;

            Volatile.Write(ref _pendingFramebufferResizeWidth, size.X);
            Volatile.Write(ref _pendingFramebufferResizeHeight, size.Y);
            Volatile.Write(ref _pendingFullInternalResizeGeneration, unchecked((long)extents.PendingFullInternalGeneration));
            Interlocked.Exchange(ref _pendingFramebufferResize, 1);
            InteractiveResizeDiagnostics.RecordResizeQueued(reason);

            Debug.RenderingEvery(
                $"XRWindow.FramebufferResize.Queued.{GetHashCode()}",
                TimeSpan.FromMilliseconds(250),
                "[XRWindow] Queued framebuffer resize hash={0} size={1}x{2} reason={3}.",
                GetHashCode(),
                size.X,
                size.Y,
                reason);
        }

        internal void BeginInteractiveResize(string reason)
        {
            Interlocked.Exchange(ref _interactiveResizeInProgress, 1);
            UpdateEffectiveWindowSize(GetCurrentWindowSize());
            PublishWindowSurfaceSnapshot(EffectiveFramebufferSize, EffectiveWindowSize, isInteractiveResize: true);
            InteractiveResizeDiagnostics.RecordCallback(reason);
        }

        internal void ApplyInteractivePresentationResize(Vector2D<int> size, string reason)
            => ApplyInteractivePresentationResize(size, null, reason);

        internal void ApplyInteractivePresentationResize(Vector2D<int> size, Vector2D<int>? windowSize, string reason)
        {
            if (size.X <= 0 || size.Y <= 0)
                return;

            UpdateEffectiveFramebufferSize(size);
            if (windowSize.HasValue)
                UpdateEffectiveWindowSize(windowSize.Value);
            else
                UpdateEffectiveWindowSize(GetCurrentWindowSize());
            PublishWindowSurfaceSnapshot(size, EffectiveWindowSize, IsInteractiveResizeInProgress);
            RecordPresentationAndOutputExtent(size);

            uint width = (uint)size.X;
            uint height = (uint)size.Y;
            foreach (XRViewport viewport in Viewports)
                viewport.SetPresentationOutputExtent(width, height);

            InteractiveResizeDiagnostics.RecordResizeQueued(reason);
        }

        internal void QueueInteractivePresentationResize(Vector2D<int> size, string reason)
            => QueueInteractivePresentationResize(size, null, reason);

        internal void QueueInteractivePresentationResize(Vector2D<int> size, Vector2D<int>? windowSize, string reason)
        {
            if (size.X <= 0 || size.Y <= 0)
                return;

            UpdateEffectiveFramebufferSize(size);
            if (windowSize.HasValue)
                UpdateEffectiveWindowSize(windowSize.Value);
            else
                UpdateEffectiveWindowSize(GetCurrentWindowSize());
            PublishWindowSurfaceSnapshot(size, EffectiveWindowSize, IsInteractiveResizeInProgress);

            int currentPending = Volatile.Read(ref _pendingInteractivePresentationResize);
            int currentWidth = Volatile.Read(ref _pendingInteractivePresentationFramebufferWidth);
            int currentHeight = Volatile.Read(ref _pendingInteractivePresentationFramebufferHeight);
            if (currentPending != 0 && currentWidth == size.X && currentHeight == size.Y)
                return;

            Volatile.Write(ref _pendingInteractivePresentationFramebufferWidth, size.X);
            Volatile.Write(ref _pendingInteractivePresentationFramebufferHeight, size.Y);
            Interlocked.Exchange(ref _pendingInteractivePresentationResize, 1);
            InteractiveResizeDiagnostics.RecordResizeQueued(reason);
        }

        internal void EndInteractiveResize(Vector2D<int> finalSize, string reason)
            => EndInteractiveResize(finalSize, null, reason);

        internal void EndInteractiveResize(Vector2D<int> finalSize, Vector2D<int>? windowSize, string reason)
        {
            Interlocked.Exchange(ref _interactiveResizeInProgress, 0);
            if (windowSize.HasValue)
                UpdateEffectiveWindowSize(windowSize.Value);
            PublishWindowSurfaceSnapshot(finalSize, EffectiveWindowSize, isInteractiveResize: false);
            QueueFramebufferResize(finalSize, windowSize, reason);
        }

        internal void CancelInteractiveResize(string reason)
        {
            Interlocked.Exchange(ref _interactiveResizeInProgress, 0);
            PublishWindowSurfaceSnapshot(EffectiveFramebufferSize, EffectiveWindowSize, isInteractiveResize: false);
            InteractiveResizeDiagnostics.RecordSuppressedRender(reason);
        }

        internal Vector2D<int> ConvertWindowSizeToFramebufferSize(Vector2D<int> windowSize)
        {
            try
            {
                Vector2D<int> origin = Window.PointToFramebuffer(Vector2D<int>.Zero);
                Vector2D<int> bottomRight = Window.PointToFramebuffer(windowSize);
                int width = Math.Abs(bottomRight.X - origin.X);
                int height = Math.Abs(bottomRight.Y - origin.Y);
                if (width > 0 && height > 0)
                    return new Vector2D<int>(width, height);
            }
            catch
            {
            }

            return new Vector2D<int>(Math.Max(1, windowSize.X), Math.Max(1, windowSize.Y));
        }

        private void UpdateEffectiveFramebufferSize(Vector2D<int> size)
        {
            if (size.X <= 0 || size.Y <= 0)
                return;

            Volatile.Write(ref _effectiveFramebufferWidth, size.X);
            Volatile.Write(ref _effectiveFramebufferHeight, size.Y);
        }

        private void UpdateEffectiveWindowSize(Vector2D<int> size)
        {
            if (size.X <= 0 || size.Y <= 0)
                return;

            Volatile.Write(ref _effectiveWindowWidth, size.X);
            Volatile.Write(ref _effectiveWindowHeight, size.Y);
        }

        private void PublishWindowSurfaceSnapshot(
            Vector2D<int> framebufferSize,
            Vector2D<int> windowSize,
            bool isInteractiveResize)
        {
            int framebufferWidth = Math.Max(0, framebufferSize.X);
            int framebufferHeight = Math.Max(0, framebufferSize.Y);
            int clientWidth = Math.Max(0, windowSize.X);
            int clientHeight = Math.Max(0, windowSize.Y);

            float dpiScaleX = clientWidth > 0
                ? framebufferWidth / (float)clientWidth
                : 1.0f;
            float dpiScaleY = clientHeight > 0
                ? framebufferHeight / (float)clientHeight
                : 1.0f;

            ulong sequence = (ulong)Interlocked.Increment(ref _windowSurfaceSnapshotSequence);
            var snapshot = new WindowSurfaceSnapshot(
                sequence,
                clientWidth,
                clientHeight,
                framebufferWidth,
                framebufferHeight,
                dpiScaleX,
                dpiScaleY,
                clientWidth <= 0 || clientHeight <= 0 || framebufferWidth <= 0 || framebufferHeight <= 0,
                isInteractiveResize,
                System.Diagnostics.Stopwatch.GetTimestamp());

            WindowResizeExtents extents = _resizeController.PublishNativeSnapshot(snapshot);
            InteractiveResizeDiagnostics.RecordSurfaceSnapshot(
                snapshot,
                extents,
                _resizeController.DroppedNativeSnapshotCount);
        }

        private void PublishWindowEventSnapshot(bool closeRequested, bool closeApproved)
        {
            ulong sequence = (ulong)Interlocked.Increment(ref _windowEventSnapshotSequence);
            var snapshot = new WindowEventSnapshot(
                sequence,
                IsFocused,
                IsWindowMinimized(),
                closeRequested,
                closeApproved,
                _isDisposed,
                _isDisposing,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                Environment.CurrentManagedThreadId);

            lock (_windowEventSnapshotSync)
                _latestWindowEventSnapshot = snapshot;
        }

        private void PublishWindowInputSnapshot()
        {
            IInputContext? input = Input;
            int keyboardCount = input?.Keyboards.Count ?? 0;
            int mouseCount = input?.Mice.Count ?? 0;
            int gamepadCount = input?.Gamepads.Count ?? 0;
            bool isFocused = IsFocused;
            bool isMouseCaptured = ResolveMouseCaptured(input);

            _ = _inputSnapshotAccumulator.Publish(
                keyboardCount,
                mouseCount,
                gamepadCount,
                isFocused,
                isMouseCaptured);
        }

        private static bool ResolveMouseCaptured(IInputContext? input)
        {
            if (input is null || input.Mice.Count <= 0)
                return false;

            try
            {
                IMouse mouse = input.Mice[0];
                return mouse.Cursor.CursorMode is CursorMode.Disabled or CursorMode.Raw ||
                    mouse.Cursor.IsConfined;
            }
            catch
            {
                return false;
            }
        }

        private bool IsWindowMinimized()
        {
            try
            {
                return Window.WindowState == WindowState.Minimized ||
                    EffectiveFramebufferSize.X <= 0 ||
                    EffectiveFramebufferSize.Y <= 0 ||
                    EffectiveWindowSize.X <= 0 ||
                    EffectiveWindowSize.Y <= 0;
            }
            catch
            {
                return EffectiveFramebufferSize.X <= 0 ||
                    EffectiveFramebufferSize.Y <= 0 ||
                    EffectiveWindowSize.X <= 0 ||
                    EffectiveWindowSize.Y <= 0;
            }
        }

        private void RecordPresentationAndOutputExtent(Vector2D<int> extent)
        {
            WindowResizeExtents extents = _resizeController.SetPresentationAndOutputExtent(extent);
            InteractiveResizeDiagnostics.RecordResizeExtents(extents);
            InteractiveResizeDiagnostics.RecordOutputScale(_resizeController.OutputScale);
        }

        private void ConsumeLatestWindowSurfaceSnapshotForRenderFrame()
        {
            if (!_resizeController.TryConsumeLatestNativeSnapshot(
                    out WindowSurfaceSnapshot snapshot,
                    out WindowResizeExtents extents))
            {
                return;
            }

            InteractiveResizeDiagnostics.RecordConsumedSurfaceSnapshot(snapshot, extents);
            if (snapshot.HasValidFramebufferExtent &&
                (!ExtentMatches(extents.PresentationExtent, snapshot.FramebufferExtent) ||
                 !ExtentMatches(extents.PipelineOutputExtent, snapshot.FramebufferExtent)))
            {
                ApplyInteractivePresentationResize(snapshot.FramebufferExtent, snapshot.ClientExtent, "native-snapshot-consumed-output");
            }

            if (!WindowResizeController.NeedsFullInternalResize(snapshot, extents))
                return;

            QueueFullInternalResize(
                snapshot.FramebufferExtent,
                force: !snapshot.IsInteractiveResize,
                snapshot.IsInteractiveResize
                    ? "native-snapshot-consumed-live-policy"
                    : "native-snapshot-consumed-settled");
        }

        private void RecordAllRenderExtents(Vector2D<int> extent)
        {
            WindowResizeExtents extents = _resizeController.SetAllRenderExtents(extent);
            InteractiveResizeDiagnostics.RecordResizeExtents(extents);
            InteractiveResizeDiagnostics.RecordOutputScale(_resizeController.OutputScale);
        }

        private void TryCommitPendingFullInternalResizeAfterRender(string reason)
        {
            WindowResizeExtents extents = _resizeController.Extents;
            Vector2D<int> pending = extents.PendingFullInternalExtent;
            ulong pendingGeneration = extents.PendingFullInternalGeneration;
            if (pendingGeneration == 0 || pending.X <= 0 || pending.Y <= 0)
                return;

            if (!AreFullInternalResizeResourcesReady(pending))
                return;

            if (!_resizeController.TryCommitPendingFullInternalExtent(
                    pendingGeneration,
                    pending,
                    out WindowResizeExtents committedExtents))
            {
                return;
            }

            InteractiveResizeDiagnostics.RecordResizeExtents(committedExtents);
            InteractiveResizeDiagnostics.RecordOutputScale(_resizeController.OutputScale);

            Debug.Rendering(
                "[XRWindow] Full-internal resize committed after render resources became active. hash={0} size={1}x{2} generation={3} reason={4}.",
                GetHashCode(),
                pending.X,
                pending.Y,
                pendingGeneration,
                reason);
        }

        private bool AreFullInternalResizeResourcesReady(Vector2D<int> pending)
        {
            foreach (XRViewport viewport in Viewports)
            {
                if (viewport.InternalWidth != pending.X ||
                    viewport.InternalHeight != pending.Y)
                {
                    return false;
                }

                var pipelineInstance = viewport.RenderPipelineInstance;
                if (pipelineInstance.PendingGeneration is not null)
                    return false;

                var activeGeneration = pipelineInstance.ActiveGeneration;
                if (activeGeneration is null)
                    continue;

                int expectedDisplayWidth = Math.Max(1, viewport.Width);
                int expectedDisplayHeight = Math.Max(1, viewport.Height);
                uint expectedInternalWidth = (uint)pending.X;
                uint expectedInternalHeight = (uint)pending.Y;
                if (activeGeneration.Key.DisplayWidth != (uint)expectedDisplayWidth ||
                    activeGeneration.Key.DisplayHeight != (uint)expectedDisplayHeight ||
                    activeGeneration.Key.InternalWidth != expectedInternalWidth ||
                    activeGeneration.Key.InternalHeight != expectedInternalHeight)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ExtentMatches(Vector2D<int> current, Vector2D<int> expected)
            => current.X == expected.X && current.Y == expected.Y;

        internal IntPtr TryGetWin32WindowHandle()
        {
            INativeWindow? native = TryGetNativeWindow();
            if (native is null)
                return IntPtr.Zero;

            object? win32 = GetNativeHandleProperty(native, nameof(INativeWindow.Win32));
            if (win32 is null)
                return IntPtr.Zero;

            object? value = GetNullableValue(win32);
            if (value is null)
                return IntPtr.Zero;

            object? hwnd = value.GetType().GetField("Item1")?.GetValue(value);
            return hwnd is IntPtr ptr ? ptr : IntPtr.Zero;
        }

        internal void RenderInteractiveResizeFrame(string reason)
            => RenderInteractiveResizeFrame(reason, allowCurrentThread: false);

        internal void RenderInteractiveResizeFrame(string reason, bool allowCurrentThread)
            => RenderInteractiveResizeFrame(reason, allowCurrentThread, deferWhenOnRenderThread: false);

        internal void RenderInteractiveResizeFrame(string reason, bool allowCurrentThread, bool deferWhenOnRenderThread)
        {
            if (_isDisposed || _isDisposing || _renderPermanentlyDisabled)
            {
                InteractiveResizeDiagnostics.RecordSuppressedRender(reason + ":window-disabled");
                return;
            }

            if (_renderDisabledUntilUtc != default && DateTime.UtcNow < _renderDisabledUntilUtc)
            {
                InteractiveResizeDiagnostics.RecordSuppressedRender(reason + ":circuit-breaker");
                return;
            }

            bool canRenderOnCurrentThread =
                (RuntimeEngine.IsRenderThread && !deferWhenOnRenderThread) ||
                (allowCurrentThread && Window.API.API == ContextAPI.OpenGL);

            if (!canRenderOnCurrentThread)
            {
                if (Interlocked.CompareExchange(ref _interactiveResizeRenderQueued, 1, 0) != 0)
                {
                    InteractiveResizeDiagnostics.RecordSuppressedRender(reason + ":queued");
                    return;
                }

                RuntimeEngine.EnqueueRenderThreadTask(
                    () =>
                    {
                        try
                        {
                            RenderInteractiveResizeFrame(reason, allowCurrentThread: false);
                        }
                        finally
                        {
                            Volatile.Write(ref _interactiveResizeRenderQueued, 0);
                        }
                    },
                    $"XRWindow.InteractiveResizeRender[{GetHashCode()}:{reason}]",
                    RenderThreadJobKind.RequiresGraphicsContext);
                return;
            }

            if (Interlocked.CompareExchange(ref _interactiveResizeRenderActive, 1, 0) != 0 ||
                Volatile.Read(ref _normalRenderActive) != 0)
            {
                InteractiveResizeDiagnostics.RecordSuppressedRender(reason + ":reentrant");
                Debug.RenderingWarningEvery(
                    $"XRWindow.InteractiveResize.Reentrant.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[InteractiveResize] Suppressed interactive render window={0} reason={1}.",
                    GetHashCode(),
                    reason);
                return;
            }

            try
            {
                ObserveRenderOwnerThread("interactive-resize-render");
                WarnIfNotRenderOwnerThread("InteractiveResize.Render");

                if (Window.API.API == ContextAPI.OpenGL)
                    Window.MakeCurrent();

                ProcessPendingInteractivePresentationResize();
                PrepareWindowBackbufferRenderArea();

                if (Volatile.Read(ref _interactiveResizeInProgress) == 0)
                    ProcessPendingFramebufferResize();

                WarnIfNotNativeWindowThread("Window.DoRender.InteractiveResize");
                using (RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.InteractiveResize.DoRender"))
                    Window.DoRender();

                TryCommitPendingFullInternalResizeAfterRender("interactive-resize-render");
                InteractiveResizeDiagnostics.RecordInteractiveRender(reason);
            }
            catch (Exception ex)
            {
                _lastRenderException = ex;
                InteractiveResizeDiagnostics.RecordSuppressedRender(reason + ":exception");
                Debug.RenderingWarningEvery(
                    $"XRWindow.InteractiveResize.Exception.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[InteractiveResize] Interactive render failed window={0} reason={1}. {2}",
                    GetHashCode(),
                    reason,
                    ex);
            }
            finally
            {
                Volatile.Write(ref _interactiveResizeRenderActive, 0);
            }
        }

        private void ProcessPendingInteractivePresentationResize()
        {
            if (Interlocked.Exchange(ref _pendingInteractivePresentationResize, 0) == 0)
                return;

            int width = Volatile.Read(ref _pendingInteractivePresentationFramebufferWidth);
            int height = Volatile.Read(ref _pendingInteractivePresentationFramebufferHeight);
            if (width <= 0 || height <= 0)
                return;

            uint viewportWidth = (uint)width;
            uint viewportHeight = (uint)height;
            foreach (XRViewport viewport in Viewports)
                viewport.SetPresentationOutputExtent(viewportWidth, viewportHeight);

            RecordPresentationAndOutputExtent(new Vector2D<int>(width, height));
        }

        private void PrepareWindowBackbufferRenderArea()
        {
            Vector2D<int> framebufferSize = EffectiveFramebufferSize;
            if (framebufferSize.X <= 0 || framebufferSize.Y <= 0)
                return;

            Renderer.BindFrameBuffer(EFramebufferTarget.Framebuffer, null);
            Renderer.SetCroppingEnabled(false);
            Renderer.SetRenderArea(new BoundingRectangle(0, 0, framebufferSize.X, framebufferSize.Y));
        }

        private Vector2D<int> GetCurrentFramebufferSize()
        {
            Vector2D<int> framebufferSize = Window.FramebufferSize;
            if (framebufferSize.X > 0 && framebufferSize.Y > 0)
                return framebufferSize;

            Vector2D<int> windowSize = Window.Size;
            return new Vector2D<int>(Math.Max(1, windowSize.X), Math.Max(1, windowSize.Y));
        }

        private Vector2D<int> GetCurrentWindowSize()
        {
            Vector2D<int> windowSize = Window.Size;
            if (windowSize.X > 0 && windowSize.Y > 0)
                return windowSize;

            Vector2D<int> framebufferSize = EffectiveFramebufferSize;
            return new Vector2D<int>(Math.Max(1, framebufferSize.X), Math.Max(1, framebufferSize.Y));
        }

        private string ResolveActualWindowingBackendName()
        {
            INativeWindow? native = TryGetNativeWindow();
            if (native is null)
                return "unknown";

            if (HasNativeHandle(native, nameof(INativeWindow.Sdl)))
                return "SDL";
            if (HasNativeHandle(native, nameof(INativeWindow.Glfw)))
                return "GLFW";
            if (HasNativeHandle(native, nameof(INativeWindow.Win32)))
                return "Win32";

            return native.Kind.ToString();
        }

        private void RefreshWindowBackendOwnership(string reason)
        {
            RuntimeWindowBackendKind backendKind = ResolveWindowBackendKind();
            RuntimeWindowBackendOwnershipInfo ownership = RuntimeWindowBackendOwnershipInfo.ForBackend(backendKind);
            _windowBackendKind = backendKind;
            _windowBackendOwnership = ownership;

            Debug.Rendering(
                "[WindowOwnership] Backend resolved window={0} backend={1} capabilities={2} windowThread={3} renderOwnerThread={4} reason={5}. {6}",
                GetHashCode(),
                backendKind,
                ownership.Capabilities,
                NativeWindowThreadId,
                RenderOwnerThreadId,
                reason,
                ownership.Notes);
        }

        private RuntimeWindowBackendKind ResolveWindowBackendKind()
        {
            INativeWindow? native = TryGetNativeWindow();
            if (native is null)
                return RuntimeWindowBackendKind.Unknown;

            if (HasNativeHandle(native, nameof(INativeWindow.Sdl)))
                return RuntimeWindowBackendKind.Sdl;
            if (HasNativeHandle(native, nameof(INativeWindow.Glfw)))
                return RuntimeWindowBackendKind.Glfw;
            if (HasNativeHandle(native, nameof(INativeWindow.Win32)))
                return RuntimeWindowBackendKind.Win32;

            return native.Kind.ToString().Equals("Win32", StringComparison.OrdinalIgnoreCase)
                ? RuntimeWindowBackendKind.Win32
                : RuntimeWindowBackendKind.Unknown;
        }

        private void ObserveRenderOwnerThread(string operation)
        {
            int currentThreadId = Environment.CurrentManagedThreadId;
            int previous = Volatile.Read(ref _renderOwnerThreadId);
            if (previous == currentThreadId)
                return;

            int observed = Interlocked.Exchange(ref _renderOwnerThreadId, currentThreadId);
            if (observed == currentThreadId)
                return;

            Debug.RenderingWarning(
                "[WindowOwnership] Render owner thread changed window={0} operation={1} previous={2} current={3} nativeWindowThread={4} backend={5}.",
                GetHashCode(),
                operation,
                observed,
                currentThreadId,
                NativeWindowThreadId,
                WindowBackendKind);
        }

        private void WarnIfNotNativeWindowThread(string operation)
        {
            int currentThreadId = Environment.CurrentManagedThreadId;
            if (currentThreadId == NativeWindowThreadId)
                return;

            Debug.RenderingWarningEvery(
                $"XRWindow.WindowThread.{GetHashCode()}.{operation}",
                TimeSpan.FromSeconds(2),
                "[WindowOwnership] Operation '{0}' for window={1} is running on thread {2}, but native window thread is {3}. Backend={4} Capabilities={5}.",
                operation,
                GetHashCode(),
                currentThreadId,
                NativeWindowThreadId,
                WindowBackendKind,
                WindowBackendOwnership.Capabilities);
        }

        private void WarnIfNotRenderOwnerThread(string operation)
        {
            int currentThreadId = Environment.CurrentManagedThreadId;
            int renderOwnerThreadId = RenderOwnerThreadId;
            if (renderOwnerThreadId == 0 || currentThreadId == renderOwnerThreadId)
                return;

            Debug.RenderingWarningEvery(
                $"XRWindow.RenderOwner.{GetHashCode()}.{operation}",
                TimeSpan.FromSeconds(2),
                "[WindowOwnership] Operation '{0}' for window={1} is running on thread {2}, but render owner thread is {3}. Backend={4}.",
                operation,
                GetHashCode(),
                currentThreadId,
                renderOwnerThreadId,
                WindowBackendKind);
        }

        private INativeWindow? TryGetNativeWindow()
            => Window is INativeWindowSource nativeSource ? nativeSource.Native : null;

        private static bool HasNativeHandle(INativeWindow native, string propertyName)
        {
            object? value = GetNativeHandleProperty(native, propertyName);
            if (value is null)
                return false;

            object? nullableValue = GetNullableValue(value);
            return nullableValue is not null;
        }

        private static object? GetNativeHandleProperty(INativeWindow native, string propertyName)
            => typeof(INativeWindow).GetProperty(propertyName)?.GetValue(native);

        private static object? GetNullableValue(object value)
        {
            Type valueType = value.GetType();
            if (Nullable.GetUnderlyingType(valueType) is null)
                return value;

            bool hasValue = (bool)(valueType.GetProperty("HasValue")?.GetValue(value) ?? false);
            return hasValue ? valueType.GetProperty("Value")?.GetValue(value) : null;
        }

        #endregion

        #region Renderer Lifecycle

        private AbstractRenderer CreateRendererForCurrentWindow(string reason)
        {
            RuntimeGraphicsApiKind apiKind = ToRuntimeGraphicsApiKind(Window.API.API);
            AbstractRenderer renderer = (AbstractRenderer)RuntimeRenderingHostServices.Current.CreateRenderer(this, apiKind);
            Debug.Rendering(
                "[XRWindow] Renderer created for hash={0}. RendererType={1} Api={2} Reason={3}",
                GetHashCode(),
                renderer.GetType().Name,
                apiKind,
                reason);
            return renderer;
        }

        private void DestroyRenderer(AbstractRenderer renderer, string reason, bool waitForGpu)
        {
            Debug.Rendering(
                "[XRWindow] Destroying renderer for hash={0}. RendererType={1} WaitForGpu={2} Reason={3}",
                GetHashCode(),
                renderer.GetType().Name,
                waitForGpu,
                reason);

            if (waitForGpu)
                TryRendererCleanupStep(renderer, reason, "WaitForGpu", renderer.WaitForGpu);

            TryRendererCleanupStep(renderer, reason, "DestroyCachedAPIRenderObjects", renderer.DestroyCachedAPIRenderObjects);
            TryRendererCleanupStep(
                renderer,
                reason,
                "DestroyObjectsForRenderer",
                () => RuntimeRenderingHostServices.Current.DestroyObjectsForRenderer(renderer));
            TryRendererCleanupStep(renderer, reason, "CleanUp", renderer.CleanUp);
        }

        private void TryRendererCleanupStep(AbstractRenderer renderer, string reason, string step, Action action)
        {
            try
            {
                using var sample = RuntimeRenderingHostServices.Current.StartProfileScope($"XRWindow.RendererCleanup.{step}");
                action();
            }
            catch (Exception ex)
            {
                Debug.RenderingWarning(
                    "[XRWindow] Renderer cleanup step failed. Window={0} RendererType={1} Step={2} Reason={3} Error={4}",
                    GetHashCode(),
                    renderer.GetType().Name,
                    step,
                    reason,
                    ex);
            }
        }

        private bool TryRecreateRendererAfterDeviceLoss(AbstractRenderer lostRenderer, string reason, Exception? exception)
        {
            if (_isDisposed || _isDisposing)
                return false;

            if (!ReferenceEquals(lostRenderer, _renderer))
                return true;

            if (!lostRenderer.IsDeviceLost)
                return false;

            if (_rendererRecreationInProgress)
                return false;

            if (_rendererRecreationAttempts >= MaxRendererRecreationAttempts)
            {
                DisableRenderingPermanently(
                    $"Renderer device-loss recovery exceeded {MaxRendererRecreationAttempts} attempts. Last reason: {reason}",
                    exception);
                return false;
            }

            _rendererRecreationInProgress = true;
            _rendererRecreationAttempts++;

            Debug.RenderingWarning(
                "[RenderDiag] Recreating renderer for existing window after device loss. Window={0} Attempt={1}/{2} RendererType={3} Api={4} Reason={5}",
                GetHashCode(),
                _rendererRecreationAttempts,
                MaxRendererRecreationAttempts,
                lostRenderer.GetType().Name,
                Window.API.API,
                reason);

            try
            {
                lostRenderer.Active = false;
                if (ReferenceEquals(AbstractRenderer.Current, lostRenderer))
                    AbstractRenderer.Current = null;

                _rendererInitialized = false;
                DestroyRenderer(lostRenderer, $"device loss recovery: {reason}", waitForGpu: true);

                AbstractRenderer replacement = CreateRendererForCurrentWindow("device loss recovery");
                try
                {
                    replacement.Initialize();
                }
                catch
                {
                    DestroyRenderer(replacement, "failed device loss recovery initialization", waitForGpu: false);
                    throw;
                }

                _renderer = replacement;
                _rendererInitialized = true;
                _renderPermanentlyDisabled = false;
                _renderPermanentlyDisabledReason = null;
                ResetRenderCircuitBreaker();
                InvalidateRendererDependentResourcesAfterRecovery();

                Debug.Rendering(
                    "[RenderDiag] Renderer recreated for existing window. Window={0} RendererType={1} Attempt={2}",
                    GetHashCode(),
                    replacement.GetType().Name,
                    _rendererRecreationAttempts);
                return true;
            }
            catch (Exception recoveryEx)
            {
                _lastRenderException = recoveryEx;
                DisableRenderingPermanently(
                    $"Renderer recreation after device loss failed. Original reason: {reason}",
                    recoveryEx);
                return false;
            }
            finally
            {
                _rendererRecreationInProgress = false;
            }
        }

        private void InvalidateRendererDependentResourcesAfterRecovery()
        {
            try
            {
                _scenePanelAdapter.InvalidateResourcesImmediate();
            }
            catch (Exception ex)
            {
                Debug.RenderingWarning(
                    "[XRWindow] Failed to invalidate scene-panel resources after renderer recovery. Window={0} Error={1}",
                    GetHashCode(),
                    ex);
            }

            foreach (var viewport in Viewports)
            {
                try
                {
                    viewport.RenderPipelineInstance.InvalidatePhysicalResources();
                }
                catch (Exception ex)
                {
                    Debug.RenderingWarning(
                        "[XRWindow] Failed to invalidate viewport pipeline resources after renderer recovery. Window={0} Viewport={1} Error={2}",
                        GetHashCode(),
                        viewport.Index,
                        ex);
                }
            }

            Vector2D<int> framebufferSize = EffectiveFramebufferSize;
            if (framebufferSize.X > 0 && framebufferSize.Y > 0)
            {
                try
                {
                    foreach (var viewport in Viewports)
                        viewport.Resize((uint)framebufferSize.X, (uint)framebufferSize.Y, setInternalResolution: true);

                    _scenePanelAdapter.OnFramebufferResized(this, framebufferSize.X, framebufferSize.Y);
                }
                catch (Exception ex)
                {
                    Debug.RenderingWarning(
                        "[XRWindow] Failed to refresh framebuffer-sized resources after renderer recovery. Window={0} Size={1}x{2} Error={3}",
                        GetHashCode(),
                        framebufferSize.X,
                        framebufferSize.Y,
                        ex);
                }
            }

            try
            {
                _renderer.FrameBufferInvalidated();
            }
            catch (Exception ex)
            {
                Debug.RenderingWarning(
                    "[XRWindow] Failed to mark renderer framebuffer invalidated after recovery. Window={0} Error={1}",
                    GetHashCode(),
                    ex);
            }
        }

        #endregion

        #region Property Changed

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(TargetWorldInstance):
                    RequestRenderStateRecheck();
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
            ResizeViewports(EffectiveWindowSize);
        }

        #endregion

        #region Public Methods - Player Registration

        public void RegisterLocalPlayer(ELocalPlayerIndex playerIndex, bool autoSizeAllViewports)
            => RegisterController(RuntimeEngine.State.GetOrCreateLocalPlayer(playerIndex), autoSizeAllViewports);

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
            IPawnController? controller = RuntimeEngine.State.GetLocalPlayer(playerIndex);
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

            Debug.Rendering("[XRWindow] Load event for hash={0}.", GetHashCode());

            //Task.Run(() =>
            //{
                Input = Window.CreateInput();
                Input.ConnectionChanged += Input_ConnectionChanged;
                SubscribeInputSnapshotEvents(Input);
                if (_interactiveResizeStrategy.IsInstalled)
                    _interactiveResizeStrategy.OnInputCreated(Input);
                PublishWindowInputSnapshot();
            //});

            if (_windowInitializationProbe is not null)
                Volatile.Write(ref _windowInitializationProbe.Stage, 2);

            Debug.Rendering("[XRWindow] Input created for hash={0}.", GetHashCode());
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
                        Debug.RenderingWarning($"[XRWindow] Window.Initialize still running after {probe.Elapsed.TotalMilliseconds:F0} ms. stage={DescribeWindowInitializationStage(Volatile.Read(ref probe.Stage))} title='{probe.Title}' api={probe.API} nativeTitleBar={probe.UseNativeTitleBar}");

                        await Task.Delay(TimeSpan.FromSeconds(5), probe.CancellationSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.RenderingWarning($"[XRWindow] Window init watchdog failed: {ex.Message}");
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
            ClosingRequested?.Invoke(this);

            Debug.Out(
                "[XRWindow] Closing requested hash={0} disposing={1} disposed={2} viewports={3}",
                GetHashCode(),
                _isDisposing,
                _isDisposed,
                Viewports.Count);
            PublishWindowEventSnapshot(closeRequested: true, closeApproved: false);

            if (!_isDisposing && !_isDisposed)
            {
                bool allowClose = RuntimeRenderingHostServices.Current.AllowWindowClose(this);
                Debug.Out("[XRWindow] Closing policy hash={0} allow={1}", GetHashCode(), allowClose);
                if (!allowClose)
                {
                    if (TryCancelCloseRequest())
                    {
                        Debug.Out("[XRWindow] Closing canceled hash={0}.", GetHashCode());
                        PublishWindowEventSnapshot(closeRequested: false, closeApproved: false);
                        return;
                    }

                    Debug.Out("[XRWindow] Closing could not be canceled hash={0}.", GetHashCode());
                }
            }

            PublishWindowEventSnapshot(closeRequested: true, closeApproved: true);
            if (IsNativeEventPumpExternallyOwned)
            {
                _approvedNativeCloseInProgress = true;
                if (!TryCancelCloseRequest())
                {
                    Debug.RenderingWarning(
                        "[XRWindow] External pump close could not be canceled before split disposal. hash={0}",
                        GetHashCode());
                }

                TryBeginExternalPumpDispose("WindowClosing");
                return;
            }

            try
            {
                _approvedNativeCloseInProgress = true;
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
            PublishWindowEventSnapshot(closeRequested: false, closeApproved: false);
            FocusChanged?.Invoke(this, focused);
            AnyWindowFocusChanged?.Invoke(this, focused);
        }

        private void FramebufferResizeCallback(Vector2D<int> obj)
        {
            FramebufferResized?.Invoke(this, obj);

            if (Volatile.Read(ref _interactiveResizeInProgress) != 0)
            {
                if (_interactiveResizeStrategy.Kind == EInteractiveWindowResizeStrategy.Win32ModalLoopTimer)
                {
                    QueueInteractivePresentationResize(obj, "framebuffer-callback-live-coalesced");
                    _interactiveResizeStrategy.OnFramebufferResizeQueued(obj);
                    return;
                }

                ApplyInteractivePresentationResize(obj, "framebuffer-callback-live");
                _interactiveResizeStrategy.OnFramebufferResizeQueued(obj);
                return;
            }

            QueueFramebufferResize(obj, "framebuffer-callback");
            _interactiveResizeStrategy.OnFramebufferResizeQueued(obj);
        }

        private void ProcessPendingFramebufferResize()
        {
            if (Interlocked.Exchange(ref _pendingFramebufferResize, 0) == 0)
                return;

            int width = Volatile.Read(ref _pendingFramebufferResizeWidth);
            int height = Volatile.Read(ref _pendingFramebufferResizeHeight);

            if (width <= 0 || height <= 0)
            {
                Debug.RenderingEvery(
                    $"XRWindow.FramebufferResize.Invalid.{GetHashCode()}",
                    TimeSpan.FromMilliseconds(250),
                    "[XRWindow] Ignoring invalid framebuffer resize hash={0} size={1}x{2}.",
                    GetHashCode(),
                    width,
                    height);
                return;
            }

            long generation = Volatile.Read(ref _pendingFullInternalResizeGeneration);
            if (generation > 0 &&
                _resizeController.IsStaleFullInternalGeneration(unchecked((ulong)generation)))
            {
                Debug.RenderingEvery(
                    $"XRWindow.FramebufferResize.StaleGeneration.{GetHashCode()}",
                    TimeSpan.FromMilliseconds(250),
                    "[XRWindow] Ignoring stale full-internal resize hash={0} size={1}x{2} generation={3}.",
                    GetHashCode(),
                    width,
                    height,
                    generation);
                return;
            }

            ApplyFramebufferResize(new Vector2D<int>(width, height));
        }

        private void ApplyFramebufferResize(Vector2D<int> obj)
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.FramebufferResize");

            try
            {
                WarnIfNotRenderOwnerThread("ApplyFramebufferResize");

                if (Window.API.API == ContextAPI.OpenGL)
                    Window.MakeCurrent();

                UpdateEffectiveFramebufferSize(obj);
                PublishWindowSurfaceSnapshot(obj, EffectiveWindowSize, IsInteractiveResizeInProgress);
                RecordPresentationAndOutputExtent(obj);

                Debug.RenderingEvery(
                    $"XRWindow.FramebufferResize.Apply.{GetHashCode()}",
                    TimeSpan.FromMilliseconds(250),
                    "[XRWindow] Applying framebuffer resize hash={0} size={1}x{2} viewports={3}.",
                    GetHashCode(),
                    obj.X,
                    obj.Y,
                    Viewports.Count);

                Viewports.ForEach(vp => vp.SetFullInternalExtent((uint)obj.X, (uint)obj.Y));

                _scenePanelAdapter.OnFramebufferResized(this, obj.X, obj.Y);

                Renderer.FrameBufferInvalidated();

                // Clear any circuit breaker backoff so the next frame renders immediately
                // with freshly recreated resources instead of waiting out old failures.
                ResetRenderCircuitBreaker();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"[XRWindow] Framebuffer resize failed for window {GetHashCode()} size={obj.X}x{obj.Y}.");
            }
        }

        private void Input_ConnectionChanged(IInputDevice device, bool connected)
        {
            switch (device)
            {
                case IKeyboard keyboard:
                    if (connected)
                        SubscribeInputSnapshotKeyboard(keyboard);
                    else
                        UnsubscribeInputSnapshotKeyboard(keyboard);
                    break;
                case IMouse mouse:
                    if (connected)
                        SubscribeInputSnapshotMouse(mouse);
                    else
                        UnsubscribeInputSnapshotMouse(mouse);

                    if (connected && Input is not null)
                        _interactiveResizeStrategy.OnInputCreated(Input);
                    break;
                case IGamepad gamepad:
                    break;
            }

            PublishWindowInputSnapshot();
        }

        private void SubscribeInputSnapshotEvents(IInputContext input)
        {
            for (int i = 0; i < input.Keyboards.Count; i++)
                SubscribeInputSnapshotKeyboard(input.Keyboards[i]);

            for (int i = 0; i < input.Mice.Count; i++)
                SubscribeInputSnapshotMouse(input.Mice[i]);
        }

        private void UnsubscribeInputSnapshotEvents(IInputContext input)
        {
            for (int i = 0; i < input.Keyboards.Count; i++)
                UnsubscribeInputSnapshotKeyboard(input.Keyboards[i]);

            for (int i = 0; i < input.Mice.Count; i++)
                UnsubscribeInputSnapshotMouse(input.Mice[i]);

            _inputSnapshotKeyboards.Clear();
            _inputSnapshotMice.Clear();
        }

        private void SubscribeInputSnapshotKeyboard(IKeyboard keyboard)
        {
            if (!_inputSnapshotKeyboards.Add(keyboard))
                return;

            keyboard.KeyDown += InputSnapshot_KeyDown;
            keyboard.KeyUp += InputSnapshot_KeyUp;
            keyboard.KeyChar += InputSnapshot_KeyChar;
        }

        private void UnsubscribeInputSnapshotKeyboard(IKeyboard keyboard)
        {
            if (!_inputSnapshotKeyboards.Remove(keyboard))
                return;

            keyboard.KeyDown -= InputSnapshot_KeyDown;
            keyboard.KeyUp -= InputSnapshot_KeyUp;
            keyboard.KeyChar -= InputSnapshot_KeyChar;
        }

        private void SubscribeInputSnapshotMouse(IMouse mouse)
        {
            if (!_inputSnapshotMice.Add(mouse))
                return;

            mouse.MouseDown += InputSnapshot_MouseDown;
            mouse.MouseUp += InputSnapshot_MouseUp;
            mouse.MouseMove += InputSnapshot_MouseMove;
            mouse.Scroll += InputSnapshot_Scroll;

            _inputSnapshotAccumulator.PrimePointerPosition(mouse.Position.X, mouse.Position.Y);
        }

        private void UnsubscribeInputSnapshotMouse(IMouse mouse)
        {
            if (!_inputSnapshotMice.Remove(mouse))
                return;

            mouse.MouseDown -= InputSnapshot_MouseDown;
            mouse.MouseUp -= InputSnapshot_MouseUp;
            mouse.MouseMove -= InputSnapshot_MouseMove;
            mouse.Scroll -= InputSnapshot_Scroll;
        }

        private void InputSnapshot_KeyDown(IKeyboard keyboard, Key key, int scanCode)
            => _inputSnapshotAccumulator.RecordKeyDown(GlfwKeyboard.Conv(key));

        private void InputSnapshot_KeyUp(IKeyboard keyboard, Key key, int scanCode)
            => _inputSnapshotAccumulator.RecordKeyUp(GlfwKeyboard.Conv(key));

        private void InputSnapshot_KeyChar(IKeyboard keyboard, char character)
            => _inputSnapshotAccumulator.RecordTextInput(character);

        private void InputSnapshot_MouseDown(IMouse mouse, MouseButton button)
        {
            if (TryMapMouseButton(button, out EMouseButton engineButton))
                _inputSnapshotAccumulator.RecordMouseDown(engineButton);
        }

        private void InputSnapshot_MouseUp(IMouse mouse, MouseButton button)
        {
            if (TryMapMouseButton(button, out EMouseButton engineButton))
                _inputSnapshotAccumulator.RecordMouseUp(engineButton);
        }

        private void InputSnapshot_MouseMove(IMouse mouse, Vector2 position)
            => _inputSnapshotAccumulator.RecordPointerPosition(position.X, position.Y);

        private void InputSnapshot_Scroll(IMouse mouse, ScrollWheel wheel)
            => _inputSnapshotAccumulator.RecordScroll(wheel.X, wheel.Y);

        private static bool TryMapMouseButton(MouseButton button, out EMouseButton engineButton)
        {
            switch (button)
            {
                case MouseButton.Left:
                    engineButton = EMouseButton.LeftClick;
                    return true;
                case MouseButton.Right:
                    engineButton = EMouseButton.RightClick;
                    return true;
                case MouseButton.Middle:
                    engineButton = EMouseButton.MiddleClick;
                    return true;
                default:
                    engineButton = default;
                    return false;
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
            w.FileDrop += OnFileDropped;
            w.Render += RenderCallback;
            w.FocusChanged += OnFocusChanged;
            w.Closing += Window_Closing;
            w.Load += Window_Load;

            // Subscribe to play mode transitions to invalidate scene panel resources
            RuntimeRenderingHostServices.Current.SubscribePlayModeTransitions(OnPlayModeTransition);
            RuntimeEngine.PlayMode.PostEnterPlay += OnPlayModeTransition;
            RuntimeEngine.PlayMode.PreExitPlay += OnPlayModeTransition;
        }

        private void UnlinkWindow()
        {
            var w = Window;
            if (w is null)
                return;

            if (_interactiveResizeStrategy.IsInstalled)
                _interactiveResizeStrategy.Uninstall();

            w.FramebufferResize -= FramebufferResizeCallback;
            w.FileDrop -= OnFileDropped;
            w.Render -= RenderCallback;
            w.FocusChanged -= OnFocusChanged;
            w.Closing -= Window_Closing;
            w.Load -= Window_Load;

            // Unsubscribe from play mode events
            RuntimeRenderingHostServices.Current.UnsubscribePlayModeTransitions(OnPlayModeTransition);
            RuntimeEngine.PlayMode.PostEnterPlay -= OnPlayModeTransition;
            RuntimeEngine.PlayMode.PreExitPlay -= OnPlayModeTransition;
        }

        private void OnFileDropped(string[] paths)
            => FileDropped?.Invoke(this, paths);

        private void OnPlayModeTransition()
        {
            bool isTransitioning = RuntimeEngine.PlayMode.IsTransitioning;
            Debug.Rendering($"[XRWindow] OnPlayModeTransition called. PlayModeState={RuntimeEngine.PlayMode.State} Viewports={Viewports.Count} Transitioning={isTransitioning}");
            
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
                Debug.Rendering($"[XRWindow] Invalidating pipeline resources for VP[{viewport.Index}] CameraComponent={viewport.CameraComponent?.Name ?? "<null>"} ActiveCamera={viewport.ActiveCamera?.GetHashCode().ToString() ?? "null"}");
                viewport.RenderPipelineInstance.InvalidatePhysicalResources();
            }

            if (isTransitioning)
                Debug.Rendering("[XRWindow] Play-mode transition is active; viewport pipelines are torn down until the state stabilizes.");
            else
                Debug.Rendering("[XRWindow] Play-mode transition settled; viewport pipelines will rebuild from retained descriptors on the next stable frame.");

            // Some rendering state (viewport size/internal resolution/aspect ratio) is only recomputed
            // on resize events. Play mode transitions can invalidate cached GPU resources without
            // any actual OS resize, leaving the window presenting stale/incorrect content until the
            // user manually resizes a panel/window.
            //
            // Force a sizing refresh against the current framebuffer dimensions to mimic that resize.
            var fb = EffectiveFramebufferSize;
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
            ObserveRenderOwnerThread("BeginTick");
            WarnIfNotRenderOwnerThread("BeginTick");

            Debug.Rendering("[XRWindow] BeginTick hash={0} viewports={1} targetWorld={2}", GetHashCode(), Viewports.Count, TargetWorldInstance?.TargetWorldName ?? "<null>");

            _renderer.Initialize();
            _rendererInitialized = true;
            RuntimeRenderingHostServices.Current.SubscribeWindowTickCallbacks(SwapBuffers, RenderFrame);

            Debug.Rendering("[XRWindow] Tick callbacks subscribed hash={0}.", GetHashCode());
        }

        private void EndTick()
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.EndTick");
            WarnIfNotRenderOwnerThread("EndTick");

            using (RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.EndTick.Unsubscribe"))
                RuntimeRenderingHostServices.Current.UnsubscribeWindowTickCallbacks(SwapBuffers, RenderFrame);
            DestroyRenderer(_renderer, "EndTick", waitForGpu: true);
            _rendererInitialized = false;
            if (!IsNativeEventPumpExternallyOwned)
            {
                using (RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.EndTick.DoEvents"))
                {
                    WarnIfNotNativeWindowThread("Window.DoEvents.EndTick");
                    Window.DoEvents();
                }

                PublishWindowInputSnapshot();
            }
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
            ObserveRenderOwnerThread("RenderFrame");
            WarnIfNotRenderOwnerThread("RenderFrame");
            ulong renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;

            long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!IsNativeEventPumpExternallyOwned)
            {
                using var eventsSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.Timer.DoEvents");
                WarnIfNotNativeWindowThread("Window.DoEvents.RenderFrame");
                Window.DoEvents();
                PublishWindowInputSnapshot();
            }
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.DoEvents", phaseStart);

            // Re-check after DoEvents in case window was closed
            if (_isDisposed || _isDisposing)
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ConsumeLatestWindowSurfaceSnapshotForRenderFrame();
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.ConsumeWindowSurfaceSnapshot", phaseStart);

            if (_isDisposed || _isDisposing)
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Volatile.Read(ref _interactiveResizeInProgress) == 0 ||
                Volatile.Read(ref _pendingFramebufferResize) != 0)
            {
                ProcessPendingFramebufferResize();
            }
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.ProcessPendingFramebufferResize", phaseStart);

            if (_isDisposed || _isDisposing)
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Volatile.Read(ref _interactiveResizeInProgress) != 0)
                ProcessPendingInteractivePresentationResize();
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.ProcessPendingInteractivePresentationResize", phaseStart);

            if (_isDisposed || _isDisposing)
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            {
                using var doRenderSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.Timer.DoRender");
                WarnIfNotNativeWindowThread("Window.DoRender.RenderFrame");
                Window.DoRender();
            }
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.DoRender", phaseStart);

            if (_isDisposed || _isDisposing)
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            TryCommitPendingFullInternalResizeAfterRender("render-frame");
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.CommitPendingFullInternalResize", phaseStart);

            if (_isDisposed || _isDisposing)
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using (var mainThreadJobsSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.Timer.PostRenderMainThreadJobs"))
            {
                // Draw the frame first, then spend a small budget on queued GPU work.
                // This keeps texture uploads and property updates from delaying visible rendering.
                RuntimeEngine.ProcessMainThreadTasks();
            }
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.PostRenderMainThreadJobs", phaseStart);

            if (_isDisposed || _isDisposing)
                return;

            // Window.Close must not run inside the active DoRender callback or the render-thread
            // job pump for the current frame; defer it until the frame boundary is complete.
            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ProcessDeferredCloseRequest();
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.ProcessDeferredCloseRequest", phaseStart);
        }

        private static void RecordRenderThreadCpuTiming(ulong frameId, string name, long startTimestamp)
            => RenderPipelineGpuProfiler.Instance.RecordRenderThreadCpuTiming(
                frameId,
                name,
                System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

        private bool ShouldBeRendering()
            => !_isDisposed && !_isDisposing && Viewports.Count > 0 && TargetWorldInstance is not null;

        private bool HasRenderableHostSurface()
        {
            var framebufferSize = EffectiveFramebufferSize;
            var windowSize = Window.Size;

            int width = Math.Max(framebufferSize.X, windowSize.X);
            int height = Math.Max(framebufferSize.Y, windowSize.Y);
            return width > 0 && height > 0;
        }

        #endregion

        #region Render Callback

        //private float _lastFrameTime = 0.0f;
        private void RenderCallback(double delta)
        {
            if (_isDisposed || _isDisposing)
                return;

            if (_renderPermanentlyDisabled)
                return;

            if (_renderDisabledUntilUtc != default && DateTime.UtcNow < _renderDisabledUntilUtc)
                return;

            if (Interlocked.CompareExchange(ref _normalRenderActive, 1, 0) != 0)
            {
                InteractiveResizeDiagnostics.RecordSuppressedRender("normal-render-reentrant");
                Debug.RenderingWarningEvery(
                    $"XRWindow.RenderCallback.Reentrant.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Suppressed normal render while another render is active. Window={0}",
                    GetHashCode());
                return;
            }

            using var frameSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.RenderFrame");
            AbstractRenderer frameRenderer = _renderer;

            try
            {
                if (frameRenderer.IsDeviceLost)
                {
                    TryRecreateRendererAfterDeviceLoss(
                        frameRenderer,
                        "renderer reported device loss before the frame began",
                        _lastRenderException);
                    return;
                }

                // Reset per-frame rendering statistics at the start of each frame.
                RuntimeRenderingHostServices.Current.BeginRenderStatsFrame();
                frameRenderer.PollGpuRenderStatsReadbacks();

                // Process any pending async buffer uploads within the frame budget.
                using (var uploadSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.ProcessPendingUploads"))
                {
                    frameRenderer.ProcessPendingUploads();
                }

                frameRenderer.Active = true;
                AbstractRenderer.Current = frameRenderer;

                bool useScenePanelMode = RuntimeRenderingHostServices.Current.IsWindowScenePanelPresentationEnabled;
                bool forceFullViewport = RuntimeRenderingHostServices.Current.ForceFullViewport;
                if (forceFullViewport)
                    useScenePanelMode = false;
                bool mirrorByComposition =
                    RuntimeRenderingHostServices.Current.IsInVR &&
                    RuntimeRenderingHostServices.Current.IsOpenXRActive &&
                    RuntimeRenderingHostServices.Current.RenderWindowsWhileInVR &&
                    RuntimeRenderingHostServices.Current.VrMirrorComposeFromEyeTextures;
                bool hasRenderableHostSurface = HasRenderableHostSurface();
                if (!hasRenderableHostSurface)
                {
                    Debug.RenderingEvery(
                        $"XRWindow.RenderCallback.ZeroSurface.{GetHashCode()}",
                        TimeSpan.FromMilliseconds(500),
                        "[RenderDiag] Skipping viewport rendering because the host surface is zero-sized. Window={0} WindowSize={1}x{2} FramebufferSize={3}x{4}",
                        GetHashCode(),
                        Window.Size.X,
                        Window.Size.Y,
                        EffectiveFramebufferSize.X,
                        EffectiveFramebufferSize.Y);
                }

                bool canRenderWindowViewports =
                    hasRenderableHostSurface &&
                    (!RuntimeRenderingHostServices.Current.IsInVR ||
                     (RuntimeRenderingHostServices.Current.RenderWindowsWhileInVR && !mirrorByComposition));

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
                Exception? viewportRenderException = null;
                try
                {
                    if (RuntimeEngine.PlayMode.IsTransitioning)
                    {
                        Debug.RenderingEvery(
                            $"XRWindow.RenderCallback.TransitionSuspended.{GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[RenderDiag] Window viewport rendering suspended during play-mode transition. Window={0} State={1} Viewports={2}",
                            GetHashCode(),
                            RuntimeEngine.PlayMode.State,
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
                    viewportRenderException = vpEx;
                    string keyBase = $"XRWindow.RenderCallback.{GetHashCode()}";
                    Debug.RenderingWarningEvery(
                        keyBase + ".ViewportException",
                        TimeSpan.FromSeconds(1),
                        "[RenderDiag] Viewport/pipeline rendering failed (Vulkan present will still run). {0}",
                        vpEx);
                }

                if (frameRenderer.IsDeviceLost)
                {
                    TryRecreateRendererAfterDeviceLoss(
                        frameRenderer,
                        "renderer reported device loss during viewport rendering",
                        viewportRenderException);
                    return;
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

                if (RuntimeEngine.StartupPresentationEnabled)
                {
                    using var startupPresentationSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.StartupPresentationMarker");
                    var framebufferSize = EffectiveFramebufferSize;
                    var fullRegion = new BoundingRectangle(0, 0, framebufferSize.X, framebufferSize.Y);
                    int markerWidth = Math.Min(96, framebufferSize.X);
                    int markerHeight = Math.Min(96, framebufferSize.Y);
                    var markerRegion = new BoundingRectangle(0, 0, markerWidth, markerHeight);

                    frameRenderer.BindFrameBuffer(EFramebufferTarget.Framebuffer, null);
                    using (frameRenderer.PushUiClipSpacePolicy())
                    {
                        frameRenderer.SetRenderArea(fullRegion);
                        frameRenderer.SetCroppingEnabled(true);
                        frameRenderer.CropRenderArea(markerRegion);
                        frameRenderer.ClearColor(RuntimeEngine.StartupPresentationClearColor);
                        frameRenderer.Clear(color: true, depth: false, stencil: false);
                        frameRenderer.SetCroppingEnabled(false);
                        frameRenderer.SetRenderArea(fullRegion);
                    }
                }

                // Allow the renderer to perform any per-window end-of-frame work (e.g., Vulkan acquire/submit/present).
                // This MUST run even when viewport rendering fails, otherwise Vulkan never presents and the window
                // shows uninitialized (white) content. With empty/partial frame ops, the Vulkan backend will at
                // minimum clear to the background color and render the debug triangle + ImGui overlay.
                using (var renderWindowSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.Renderer.RenderWindow"))
                {
                    frameRenderer.RenderWindow(delta);
                }

                using (var postViewportsSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.PostRenderViewportsCallback"))
                {
                    PostRenderViewportsCallback?.Invoke();
                }

                // Tick render-thread coroutines (e.g. progressive texture uploads) while the renderer
                // is still marked active so that IsRendererActive guards inside those coroutines pass.
                using (var inFrameJobsSample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.InFrameMainThreadJobs"))
                {
                    RuntimeEngine.ProcessMainThreadTasks();
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

                if (frameRenderer.IsDeviceLost)
                {
                    TryRecreateRendererAfterDeviceLoss(
                        frameRenderer,
                        "renderer reported device loss during the frame",
                        ex);
                    return;
                }

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
                frameRenderer.Active = false;
                if (ReferenceEquals(AbstractRenderer.Current, frameRenderer))
                    AbstractRenderer.Current = null;
                Volatile.Write(ref _normalRenderActive, 0);
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
                RuntimeEngine.EditorPreferences.ViewportPresentationMode,
                canRenderWindowViewports,
                Viewports.Count,
                TargetWorldInstance?.TargetWorldName ?? "<null>",
                TargetWorldInstance?.IsPlaySessionActive.ToString() ?? "<null>",
                delta,
                RuntimeEngine.Rendering.Stats.Frame.DrawCalls,
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanRequestedDraws,
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanCulledDraws,
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanEmittedIndirectDraws,
                RuntimeEngine.Rendering.Stats.Vulkan.VulkanConsumedDraws,
                RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyOpaqueOrOtherVisible,
                RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyMaskedVisible,
                RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyApproximateVisible,
                RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyExactVisible);

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
                    var fb = EffectiveFramebufferSize;
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
            RequestRenderStateRecheck();
        }

        private XRViewport AddViewportForPlayer(IPawnController? controller, bool autoSizeAllViewports)
        {
            using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("XRWindow.AddViewportForPlayer");

            XRViewport newViewport = XRViewport.ForTotalViewportCount(this, Viewports.Count);
            newViewport.AssociatedPlayer = controller;
            Viewports.Add(newViewport);

            Debug.Rendering("Added new viewport to {0}: {1}", GetType().GetFriendlyName(), newViewport.Index);

            Debug.Rendering(
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

        private bool TryBeginExternalPumpDispose(string reason)
        {
            if (!IsNativeEventPumpExternallyOwned)
                return false;

            if (_isDisposed)
                return true;

            if (Interlocked.Exchange(ref _externalPumpDisposeStarted, 1) != 0)
                return true;

            _isDisposing = true;
            Debug.Rendering(
                "[XRWindow] Beginning split external-pump disposal. hash={0} reason={1} renderOwnerThread={2} nativeWindowThread={3}",
                GetHashCode(),
                reason,
                RenderOwnerThreadId,
                NativeWindowThreadId);

            if (RuntimeEngine.IsRenderThread)
            {
                DisposeExternalPumpRenderResources(reason);
            }
            else
            {
                RuntimeEngine.EnqueueRenderThreadTask(
                    () => DisposeExternalPumpRenderResources(reason),
                    $"XRWindow.DisposeExternalPump.Render[{GetHashCode()}:{reason}]",
                    RenderThreadJobKind.RequiresGraphicsContext);
            }

            return true;
        }

        private void DisposeExternalPumpRenderResources(string reason)
        {
            if (_isDisposed)
                return;

            try
            {
                WarnIfNotRenderOwnerThread("DisposeExternalPump.RenderResources");

                if (IsTickLinked)
                {
                    IsTickLinked = false;
                    try
                    {
                        EndTick();
                    }
                    catch (Exception ex)
                    {
                        Debug.RenderingWarning(
                            "[XRWindow] EndTick failed during external-pump disposal. hash={0} reason={1} error={2}",
                            GetHashCode(),
                            reason,
                            ex);
                    }
                }
                else if (_rendererInitialized)
                {
                    DestroyRenderer(_renderer, "DisposeExternalPump", waitForGpu: true);
                    _rendererInitialized = false;
                }

                bool skipNativeWindowDispose = _approvedNativeCloseInProgress &&
                    _renderer.ShouldSkipNativeWindowDisposeForShutdown;

                if (skipNativeWindowDispose)
                {
                    Debug.Rendering(
                        "[XRWindow] Fast shutdown skipping scene-panel/native window dispose because renderer abandoned async shutdown work. hash={0}",
                        GetHashCode());
                }
                else
                {
                    try
                    {
                        _scenePanelAdapter.Dispose();
                    }
                    catch
                    {
                    }
                }

                if (_interactiveResizeStrategy.IsInstalled)
                {
                    try
                    {
                        _interactiveResizeStrategy.Uninstall();
                    }
                    catch
                    {
                    }
                }

                try
                {
                    Viewports.Clear();
                }
                catch
                {
                }
            }
            finally
            {
                RuntimeRenderingHostServices.Current.EnqueueWindowThreadTask(
                    this,
                    () => DisposeExternalPumpNativeResources(reason),
                    $"XRWindow.DisposeExternalPump.Native[{GetHashCode()}:{reason}]");
            }
        }

        private void DisposeExternalPumpNativeResources(string reason)
        {
            if (_isDisposed)
                return;

            try
            {
                WarnIfNotNativeWindowThread("DisposeExternalPump.NativeResources");

                if (Input is not null)
                {
                    try
                    {
                        UnsubscribeInputSnapshotEvents(Input);
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

                UnlinkWindow();

                bool skipNativeWindowDispose = _approvedNativeCloseInProgress &&
                    _renderer.ShouldSkipNativeWindowDisposeForShutdown;

                if (!skipNativeWindowDispose)
                {
                    try
                    {
                        (Window as IDisposable)?.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                CompleteDispose();
                RuntimeEngine.EnqueueRenderThreadTask(
                    () => RuntimeRenderingHostServices.Current.RemoveWindow(this),
                    $"XRWindow.RemoveExternalPumpWindow[{GetHashCode()}:{reason}]");
            }
        }

        private void CompleteDispose()
        {
            Interlocked.Exchange(ref _pendingCloseRequested, 0);
            Interlocked.Exchange(ref _externalNativeEventPumpActive, 0);
            _approvedNativeCloseInProgress = false;
            _isDisposed = true;
            _isDisposing = false;
            PublishWindowEventSnapshot(closeRequested: false, closeApproved: true);
            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            if (TryBeginExternalPumpDispose("Dispose"))
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
                    DestroyRenderer(_renderer, "Dispose", waitForGpu: true);
                    _rendererInitialized = false;
                }

                bool skipNativeWindowDispose = _approvedNativeCloseInProgress &&
                    _renderer.ShouldSkipNativeWindowDisposeForShutdown;

                if (skipNativeWindowDispose)
                {
                    Debug.Rendering(
                        "[XRWindow] Fast shutdown skipping scene-panel/native window dispose because renderer abandoned async shutdown work. hash={0}",
                        GetHashCode());
                }
                else
                {
                    // Free dockable scene-panel GPU resources.
                    _scenePanelAdapter.Dispose();
                }

                if (_interactiveResizeStrategy.IsInstalled)
                {
                    try
                    {
                        _interactiveResizeStrategy.Uninstall();
                    }
                    catch
                    {
                    }
                }

                // Unhook input.
                if (Input is not null)
                {
                    try
                    {
                        UnsubscribeInputSnapshotEvents(Input);
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

                if (!skipNativeWindowDispose)
                {
                    // Finally, release the window itself if possible.
                    try
                    {
                        (Window as IDisposable)?.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                CompleteDispose();
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

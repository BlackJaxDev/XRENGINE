using XREngine.Extensions;
using Newtonsoft.Json;
using Silk.NET.Maths;
using System.Collections.Generic;
using System.Threading;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.Occlusion;
using XREngine.Scene;

namespace XREngine.Rendering
{
    /// <summary>
    /// Connects an owned desktop window backend to an API-specific engine renderer.
    /// </summary>
    [RuntimeOnly]
    public sealed class XRWindow : XRBase, IRuntimeRenderWindowHost, IDisposable
    {
        private long _completedRenderWindowIntervalSequence;
        public XRWindowCompletedRenderInterval LastCompletedRenderWindowInterval { get; private set; }
        private static readonly TimeSpan RendererShutdownGpuWaitTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Creates the runtime-owned adapter used to present this window in an editor scene panel.
        /// </summary>
        public static IRuntimeWindowScenePanelAdapter CreateScenePanelAdapter()
            => new XRWindowScenePanelAdapter();

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

        private sealed class DesktopWindowEventSink(XRWindow owner) : IRuntimeWindowEventSink
        {
            public void SurfaceChanged(WindowSurfaceSnapshot snapshot)
                => owner.FramebufferResizeCallback(snapshot.FramebufferExtent);

            public void FocusChanged(bool focused) => owner.OnFocusChanged(focused);
            public void FileDropped(string[] paths) => owner.OnFileDropped(paths);
            public void KeyDown(EKey key) => AnyWindowKeyDown?.Invoke(owner, key);
            public bool CloseRequested() => owner.HandleDesktopCloseRequested();
            public void InteractiveResizeStarted() => owner.BeginInteractiveResize("native-window");
            public void InteractiveResizeUpdated(IVector2 extent)
                => owner.QueueInteractivePresentationResize(new Vector2D<int>(extent.X, extent.Y), "native-window");
            public void InteractiveResizeEnded()
                => owner.EndInteractiveResize(owner.GetCurrentFramebufferSize(), "native-window");
            public void RepaintRequested()
            {
                if (owner._renderer is null)
                    return;
                if (owner.NativeWindowThreadId == owner.RenderOwnerThreadId)
                    owner.RenderInteractiveResizeFrame("desktop-native-resize", allowCurrentThread: true);
                else
                    owner.RequestRenderStateRecheck();
            }
            public void RenderRequested(double deltaSeconds)
            {
                if (owner._renderer is not null)
                    owner.RenderCallback(deltaSeconds);
            }
        }

        #endregion

        #region Events

        public static event Action<XRWindow, bool>? AnyWindowFocusChanged;
        public static event Action<XRWindow, long>? AnyRendererFrameCompleted;
        /// <summary>
        /// Fired for every native-window key-down transition before pawn input consumes the
        /// corresponding snapshot. Handlers must remain lightweight and marshal state changes
        /// to their owning thread.
        /// </summary>
        public static event Action<XRWindow, EKey>? AnyWindowKeyDown;
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

        #endregion

        private bool _isDisposing;
        private bool _isDisposed;
        private bool _approvedNativeCloseInProgress;
        private int _closeRequestedOrApproved;
        private int _pendingCloseRequested;
        private int _approvedNativeCloseCompletionPending;
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
        private int _renderSurfaceLatchActive;
        private int _renderSurfaceFramebufferWidth;
        private int _renderSurfaceFramebufferHeight;
        private int _renderSurfaceWindowWidth;
        private int _renderSurfaceWindowHeight;
        private int _interactiveResizeInProgress;
        private int _interactiveResizeRenderActive;
        private int _interactiveResizeRenderQueued;
        private int _normalRenderActive;
        private int _externalNativeEventPumpActive;
        private int _externalPumpDisposeStarted;
        private readonly int _nativeWindowThreadId;
        private int _renderOwnerThreadId;
        private long _windowSurfaceSnapshotSequence;
        private long _windowEventSnapshotSequence;
        private readonly object _windowEventSnapshotSync = new();
        private WindowEventSnapshot _latestWindowEventSnapshot;
        private IRuntimeWindowBackend? _desktopBackend;
        private readonly WindowResizeController _resizeController = new();
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
        /// Interface to render a scene for this window using the requested graphics API.
        /// </summary>
        public AbstractRenderer Renderer => _renderer;

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

        public string ActualWindowingBackendName => _desktopBackend?.Kind.ToString() ?? "Unknown";

        public string WindowTitle => _desktopBackend?.Title ?? string.Empty;

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
                return _desktopBackend?.Input ?? default;
            }
        }

        public IRuntimeWindowGlContext? DesktopGlContext => _desktopBackend?.GlContext;
        public IRuntimeWindowVulkanSurface? DesktopVulkanSurface => _desktopBackend?.VulkanSurface;
        public IRuntimeWindowBackend? DesktopWindowBackend => _desktopBackend;
        public bool UsesDesktopWindowBackend => _desktopBackend is not null;
        public WindowInputSnapshot ConsumeUiInputSnapshot()
            => _desktopBackend?.ConsumeUiInput() ?? default;

        public WindowInputSnapshot ConsumeUiInputSnapshot(List<WindowInputEvent> orderedDestination)
            => _desktopBackend?.ConsumeUiInput(orderedDestination) ?? default;
        public nint PlatformWindowHandle => _desktopBackend?.PlatformWindowHandle ?? 0;
        public nint OperatingSystemWindowHandle => _desktopBackend?.OperatingSystemWindowHandle ?? 0;

        /// <summary>
        /// Returns and acknowledges all transient input published for the update-side consumer.
        /// </summary>
        internal WindowInputSnapshot ConsumeLatestWindowInputSnapshot()
            => _desktopBackend?.ConsumeInput() ?? default;

        /// <summary>
        /// Returns the current native-window state for an engine keyboard key.
        /// Unlike pawn input, this state remains available when no gameplay pawn is possessed.
        /// </summary>
        public bool IsKeyPressed(EKey key)
        {
            ReadOnlySpan<EKey> pressed = LatestWindowInputSnapshot.PressedKeySpan;
            for (int i = 0; i < pressed.Length; i++)
            {
                if (pressed[i] == key)
                    return true;
            }
            return false;
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

        /// <summary>
        /// Returns the framebuffer extent latched at the start of the current render frame.
        /// Native resize callbacks may continue publishing newer extents while the frame is
        /// recorded, but those must apply to the next frame as one coherent unit.
        /// </summary>
        public Vector2D<int> RenderFramebufferSize
        {
            get
            {
                if (Volatile.Read(ref _renderSurfaceLatchActive) != 0)
                {
                    int width = Volatile.Read(ref _renderSurfaceFramebufferWidth);
                    int height = Volatile.Read(ref _renderSurfaceFramebufferHeight);
                    if (width > 0 && height > 0)
                        return new Vector2D<int>(width, height);
                }

                return EffectiveFramebufferSize;
            }
        }

        /// <summary>
        /// Returns the client extent latched with <see cref="RenderFramebufferSize"/>.
        /// </summary>
        public Vector2D<int> RenderWindowSize
        {
            get
            {
                if (Volatile.Read(ref _renderSurfaceLatchActive) != 0)
                {
                    int width = Volatile.Read(ref _renderSurfaceWindowWidth);
                    int height = Volatile.Read(ref _renderSurfaceWindowHeight);
                    if (width > 0 && height > 0)
                        return new Vector2D<int>(width, height);
                }

                return EffectiveWindowSize;
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
               _desktopBackend is not null &&
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
            if (_renderer is not null)
            {
                _renderer.RequestFrameAdmissionRecovery(
                    "XRWindow render circuit breaker reset after an explicit render-state change");
            }
        }

        private void DisableRenderingPermanently(string reason, Exception? exception = null)
        {
            if (_renderPermanentlyDisabled)
                return;

            if (_rendererRecreationInProgress)
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

        /// <summary>Requests a client-area resize on this window's owning thread.</summary>
        public void RequestResize(int width, int height)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            if (_isDisposed || _isDisposing)
                return;

            if (IsNativeEventPumpExternallyOwned)
            {
                RuntimeRenderingHostServices.Scheduling.EnqueueWindowThreadTask(
                    this,
                    () => ResizeOnWindowThread(width, height),
                    "XRWindow.RequestResize");
                return;
            }

            if (RuntimeEngine.IsRenderThread)
                ResizeOnWindowThread(width, height);
            else
                RuntimeEngine.EnqueueRenderThreadTask(
                    () => ResizeOnWindowThread(width, height),
                    "XRWindow.RequestResize",
                    RenderThreadJobKind.RequiresGraphicsContext);
        }

        private void ResizeOnWindowThread(int width, int height)
        {
            if (_isDisposed || _isDisposing)
                return;
            WarnIfNotNativeWindowThread("Window.Size");
            _desktopBackend?.RequestSize(new IVector2(width, height));
        }

        public void RequestClose()
        {
            if (_isDisposed || _isDisposing)
                return;

            MarkCloseRequestedOrApproved();

            if (IsNativeEventPumpExternallyOwned)
            {
                RuntimeRenderingHostServices.Scheduling.EnqueueWindowThreadTask(
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
                RuntimeRenderingHostServices.Scheduling.EnqueueWindowThreadTask(
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
            _desktopBackend?.RequestCursorCapture(captured);
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
            if (TryCompleteApprovedNativeClose())
                return;

            if (_isDisposed || _isDisposing)
                return;

            WarnIfNotNativeWindowThread("Window.DoEvents.WindowPumpHost");

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.WindowPumpHost.DoEvents"))
            {
                _desktopBackend?.PumpEvents();
            }

            // A close approved inside this pump completes here, after the native callback has unwound.
            if (TryCompleteApprovedNativeClose())
                return;

            if (_isDisposed || _isDisposing)
                return;

            Vector2D<int> framebufferSize = GetCurrentFramebufferSize();
            Vector2D<int> windowSize = GetCurrentWindowSize();
            UpdateEffectiveFramebufferSize(framebufferSize);
            UpdateEffectiveWindowSize(windowSize);
            PublishWindowSurfaceSnapshot(framebufferSize, windowSize, IsInteractiveResizeInProgress);
            PublishWindowEventSnapshot(closeRequested: false, closeApproved: false);
        }

        private void ApplyVSyncModeOnRenderThread(EVSyncMode globalVSyncMode)
        {
            if (_isDisposed || _isDisposing)
                return;

            WarnIfNotRenderOwnerThread("ApplyVSyncMode");

            bool enableVSync = WindowVSyncRequested || globalVSyncMode != EVSyncMode.Off;
            bool isOpenGlWindow = _desktopBackend?.GraphicsApi == RuntimeGraphicsApiKind.OpenGL;

            try
            {
                IRuntimeWindowBackend backend = _desktopBackend
                    ?? throw new InvalidOperationException("No desktop window backend is attached.");
                if (isOpenGlWindow)
                    backend.GlContext?.MakeCurrent();
                backend.SetVSync(enableVSync);
                if (isOpenGlWindow && enableVSync && globalVSyncMode == EVSyncMode.Adaptive)
                {
                    try { backend.GlContext?.SetSwapInterval(-1); }
                    catch { backend.GlContext?.SetSwapInterval(1); }
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

            MarkCloseRequestedOrApproved();

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

            MarkCloseRequestedOrApproved();
            WarnIfNotNativeWindowThread("Window.Close");
            PerformCloseRequest();
        }

        private void ProcessDeferredCloseRequest()
        {
            if (Interlocked.Exchange(ref _pendingCloseRequested, 0) == 0)
                return;

            if (IsNativeEventPumpExternallyOwned)
            {
                RuntimeRenderingHostServices.Scheduling.EnqueueWindowThreadTask(
                    this,
                    RequestCloseOnWindowThread,
                    $"Viewport.CloseWindow.DeferredWindowThread[{GetHashCode()}]");
                return;
            }

            PerformCloseRequest();
        }

        private void PerformCloseRequest()
        {
            MarkCloseRequestedOrApproved();
            try
            {
                _desktopBackend?.RequestClose();
            }
            catch (Exception ex)
            {
                ClearCloseRequestedOrApproved();
                Debug.RenderingWarning(
                    "[XRWindow] Window.Close failed for hash={0}. {1}",
                    GetHashCode(),
                    ex);
            }
        }

        private void MarkCloseRequestedOrApproved()
            => Interlocked.Exchange(ref _closeRequestedOrApproved, 1);

        private void ClearCloseRequestedOrApproved()
            => Interlocked.Exchange(ref _closeRequestedOrApproved, 0);

        private bool IsCloseRequestedOrApproved
            => Volatile.Read(ref _closeRequestedOrApproved) != 0;

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

        public XRWindow(RuntimeWindowCreateOptions options)
        {
            IsSecondaryGpuContext = options.Purpose == RuntimeWindowPurpose.SecondaryGpuContext;
            _viewports.CollectionChanged += ViewportsChanged;
            _scenePanelAdapter = RuntimeRenderingHostServices.Factories.CreateWindowScenePanelAdapter();
            InteractiveResizeStrategy = options.ResizeStrategy;
            UseNativeTitleBar = options.Startup.UseNativeTitleBar;
            WindowVSyncRequested = options.Startup.VSync;
            PreferHDROutput = options.PreferHdrOutput;

            _desktopBackend = RuntimeWindowBackendRegistry.RequireFactory().Create(in options);
            _nativeWindowThreadId = _desktopBackend.OwnerThreadId;
            Volatile.Write(ref _renderOwnerThreadId, _nativeWindowThreadId);
            try
            {
                LinkWindow();
                _desktopBackend.Initialize(new DesktopWindowEventSink(this));
                UpdateEffectiveFramebufferSize(GetCurrentFramebufferSize());
                UpdateEffectiveWindowSize(GetCurrentWindowSize());
                RefreshWindowBackendOwnership("post-initialize");
                PublishWindowSurfaceSnapshot(
                    EffectiveFramebufferSize,
                    EffectiveWindowSize,
                    IsInteractiveResizeInProgress);
                PublishWindowEventSnapshot(closeRequested: false, closeApproved: false);
                RecordAllRenderExtents(EffectiveFramebufferSize);
                _renderer = CreateRendererForCurrentWindow("initial desktop window construction");
            }
            catch
            {
                UnlinkWindow();
                _desktopBackend.Dispose();
                _scenePanelAdapter.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Gets whether this hidden window owns the optional background GPU context rather than a presentation renderer.
        /// Process-global presentation middleware such as Streamline must not bind to this context.
        /// </summary>
        public bool IsSecondaryGpuContext { get; }

        #endregion

        #region Interactive Resize

        public void SetInteractiveResizeStrategy(EInteractiveWindowResizeStrategy strategy)
        {
            if (_isDisposed || _isDisposing || strategy == InteractiveResizeStrategy)
                return;

            if (_desktopBackend is { } backend)
            {
                if (Environment.CurrentManagedThreadId != backend.OwnerThreadId)
                {
                    RuntimeRenderingHostServices.Scheduling.EnqueueWindowThreadTask(
                        this,
                        () => SetInteractiveResizeStrategy(strategy),
                        $"XRWindow.SetInteractiveResizeStrategy[{GetHashCode()}]");
                    return;
                }

                if (strategy == EInteractiveWindowResizeStrategy.SdlBackend ||
                    InteractiveResizeStrategy == EInteractiveWindowResizeStrategy.SdlBackend)
                    Debug.RenderingWarning(
                        "[InteractiveResize] Runtime strategy changed from={0} to={1}; native window backend remains {2} until recreation.",
                        InteractiveResizeStrategy, strategy, ActualWindowingBackendName);

                backend.SetInteractiveResizeStrategy(strategy);
                InteractiveResizeStrategy = strategy;
            }
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

            // An admitted request owns a new controller generation. Always refresh the
            // complete queued tuple even when its dimensions match an older callback;
            // retaining the old stamp makes ProcessPendingFramebufferResize discard the
            // only queued resize as stale.
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

            // WSI present scaling keeps the prior swapchain generation visible while
            // the final full-internal resize waits for the drag to settle.
            if (!IsInteractiveResizeInProgress)
                Renderer.FrameBufferInvalidated();

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
            if (_desktopBackend is { } backend)
            {
                WindowSurfaceSnapshot surface = backend.Surface;
                float dpiX = surface.DpiScaleX > 0.0f ? surface.DpiScaleX : 1.0f;
                float dpiY = surface.DpiScaleY > 0.0f ? surface.DpiScaleY : 1.0f;
                return new Vector2D<int>(
                    Math.Max(1, (int)MathF.Round(windowSize.X * dpiX)),
                    Math.Max(1, (int)MathF.Round(windowSize.Y * dpiY)));
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
            bool closeRequestedOrApproved = closeRequested || closeApproved || IsCloseRequestedOrApproved;
            ulong sequence = (ulong)Interlocked.Increment(ref _windowEventSnapshotSequence);
            var snapshot = new WindowEventSnapshot(
                sequence,
                IsFocused,
                IsWindowMinimized(),
                closeRequestedOrApproved,
                closeApproved,
                _isDisposed,
                _isDisposing,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                Environment.CurrentManagedThreadId);

            lock (_windowEventSnapshotSync)
                _latestWindowEventSnapshot = snapshot;
        }


        private bool IsWindowMinimized()
        {
            try
            {
                return (_desktopBackend?.Surface.IsMinimized ?? false) ||
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
                if (snapshot.IsInteractiveResize)
                {
                    QueueInteractivePresentationResize(
                        snapshot.FramebufferExtent,
                        snapshot.ClientExtent,
                        "native-snapshot-consumed-output");
                }
                else
                {
                    ApplyInteractivePresentationResize(
                        snapshot.FramebufferExtent,
                        snapshot.ClientExtent,
                        "native-snapshot-consumed-output");
                }
            }

            if (!WindowResizeController.NeedsFullInternalResize(snapshot, extents))
                return;

            // Keep the last complete internal resource generation alive during a
            // native border drag. UI layout, camera aspect, and presentation still
            // follow the transient extent, while WM_EXITSIZEMOVE queues the exact
            // final full-internal resize. Admitting a heavy generation for every
            // transient pixel prevents Vulkan presentation from ever converging.
            if (snapshot.IsInteractiveResize)
                return;

            QueueFullInternalResize(
                snapshot.FramebufferExtent,
                force: true,
                "native-snapshot-consumed-settled");
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
            WindowResizeExtents extents = _resizeController.Extents;
            if (!ExtentMatches(extents.PresentationExtent, pending) ||
                !ExtentMatches(extents.PipelineOutputExtent, pending))
            {
                return false;
            }

            foreach (XRViewport viewport in Viewports)
            {
                if (viewport.Width <= 0 || viewport.Height <= 0 ||
                    viewport.InternalWidth <= 0 || viewport.InternalHeight <= 0)
                    return false;

                var pipelineInstance = viewport.RenderPipelineInstance;
                if (!pipelineInstance.IsCurrentResourceProfileReady(viewport))
                    return false;
            }

            return true;
        }

        private static bool ExtentMatches(Vector2D<int> current, Vector2D<int> expected)
            => current.X == expected.X && current.Y == expected.Y;

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

            int currentThreadId = Environment.CurrentManagedThreadId;
            bool isRenderOwnerThread = currentThreadId == RenderOwnerThreadId;
            bool canRenderOnCurrentThread = isRenderOwnerThread &&
                ((RuntimeEngine.IsRenderThread && !deferWhenOnRenderThread) || allowCurrentThread);

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

            if (Interlocked.CompareExchange(ref _interactiveResizeRenderActive, 1, 0) != 0)
            {
                InteractiveResizeDiagnostics.RecordSuppressedRender("interactive-active");
                long activeSince = InteractiveResizeDiagnostics.CallbackActiveSinceTimestamp;
                TimeSpan activeFor = activeSince == 0L
                    ? TimeSpan.Zero
                    : System.Diagnostics.Stopwatch.GetElapsedTime(activeSince);
                Debug.RenderingWarningEvery(
                    $"XRWindow.InteractiveResize.InteractiveActive.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[InteractiveResize] Suppressed interactive render window={0} reason={1} cause=interactive-active activeMs={2:F3} lastOutcome={3} lastDispatchReason={4}.",
                    GetHashCode(),
                    reason,
                    activeFor.TotalMilliseconds,
                    InteractiveResizeDiagnostics.LastDispatchOutcome,
                    InteractiveResizeDiagnostics.LastDispatchReason);
                return;
            }

            if (Volatile.Read(ref _normalRenderActive) != 0)
            {
                Volatile.Write(ref _interactiveResizeRenderActive, 0);
                InteractiveResizeDiagnostics.RecordSuppressedRender("normal-render-active");
                Debug.RenderingWarningEvery(
                    $"XRWindow.InteractiveResize.NormalRenderActive.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[InteractiveResize] Suppressed interactive render window={0} reason={1} cause=normal-render-active.",
                    GetHashCode(),
                    reason);
                return;
            }

            try
            {
                long callbackStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                InteractiveResizeDiagnostics.RecordCallbackEntry(callbackStarted);
                ObserveRenderOwnerThread("interactive-resize-render");
                WarnIfNotRenderOwnerThread("InteractiveResize.Render");

                ulong? presentationPackageId = null;
                EInteractiveResizeDispatchReason presentationUnavailableReason =
                    EInteractiveResizeDispatchReason.None;
                if (((IRuntimeRendererHost)_renderer).TryGetBackendCapability<IInteractiveResizePresentationBackendCapability>(out var presentationCapability) &&
                    presentationCapability is not null)
                {
                    if (!presentationCapability.TryGetInteractiveResizePresentationPackage(
                            out ulong packageId,
                            out EInteractiveResizeDispatchReason unavailableReason))
                    {
                        presentationUnavailableReason = unavailableReason;
                    }
                    else
                    {
                        presentationPackageId = packageId;
                    }
                }

                InteractiveResizeDispatchResult dispatch =
                    RuntimeRenderingHostServices.Scheduling.TryDispatchInteractiveResizeFrame(presentationPackageId);
                if (!dispatch.Presented &&
                    presentationUnavailableReason != EInteractiveResizeDispatchReason.None &&
                    dispatch.Reason is EInteractiveResizeDispatchReason.VisibilityUnavailable or
                        EInteractiveResizeDispatchReason.FrameDidNotAdvance)
                {
                    dispatch = dispatch with { Reason = presentationUnavailableReason };
                }
                InteractiveResizeDiagnostics.RecordDispatch(dispatch);
                if (!dispatch.Presented)
                {
                    InteractiveResizeDiagnostics.RecordSuppressedRender(
                        GetInteractiveResizeDispatchReasonName(dispatch.Reason));
                    return;
                }

                InteractiveResizeDiagnostics.RecordInteractiveRender(
                    dispatch.Outcome == EInteractiveResizeDispatchOutcome.PresentedScaledStale
                        ? "scaled-stale"
                        : reason);
            }
            catch (Exception ex)
            {
                _lastRenderException = ex;
                InteractiveResizeDiagnostics.RecordDispatch(new(
                    EInteractiveResizeDispatchOutcome.Faulted,
                    EInteractiveResizeDispatchReason.Exception,
                    RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId,
                    ElapsedStopwatchTicks: 0L));
                InteractiveResizeDiagnostics.RecordSuppressedRender("exception");
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
                long activeSince = InteractiveResizeDiagnostics.CallbackActiveSinceTimestamp;
                if (activeSince != 0L)
                {
                    InteractiveResizeDiagnostics.RecordCallbackExit(
                        System.Diagnostics.Stopwatch.GetTimestamp() - activeSince);
                }
                Volatile.Write(ref _interactiveResizeRenderActive, 0);
            }
        }

        private static string GetInteractiveResizeDispatchReasonName(
            EInteractiveResizeDispatchReason reason)
            => reason switch
            {
                EInteractiveResizeDispatchReason.RuntimeStopped => "runtime-stopped",
                EInteractiveResizeDispatchReason.WrongThread => "wrong-thread",
                EInteractiveResizeDispatchReason.FrameAlreadyActive => "frame-already-active",
                EInteractiveResizeDispatchReason.RenderCadenceNotDue => "render-cadence-not-due",
                EInteractiveResizeDispatchReason.VisibilityUnavailable => "visibility-unavailable",
                EInteractiveResizeDispatchReason.FrameDidNotAdvance => "frame-did-not-advance",
                EInteractiveResizeDispatchReason.Exception => "exception",
                _ => "none",
            };

        private void ProcessPendingInteractivePresentationResize()
        {
            if (Interlocked.Exchange(ref _pendingInteractivePresentationResize, 0) == 0)
                return;

            int width = Volatile.Read(ref _pendingInteractivePresentationFramebufferWidth);
            int height = Volatile.Read(ref _pendingInteractivePresentationFramebufferHeight);
            if (width <= 0 || height <= 0)
                return;

            ApplyInteractivePresentationResize(
                new Vector2D<int>(width, height),
                "interactive-presentation-resize-queued");
        }

        private void PrepareWindowBackbufferRenderArea()
        {
            Vector2D<int> framebufferSize = RenderFramebufferSize;
            if (framebufferSize.X <= 0 || framebufferSize.Y <= 0)
                return;

            Renderer.BindFrameBuffer(EFramebufferTarget.Framebuffer, null);
            Renderer.SetCroppingEnabled(false);
            Renderer.SetRenderArea(new BoundingRectangle(0, 0, framebufferSize.X, framebufferSize.Y));
        }

        private Vector2D<int> GetCurrentFramebufferSize()
        {
            WindowSurfaceSnapshot snapshot = _desktopBackend?.Surface ?? default;
            int width = snapshot.FramebufferWidth > 0 ? snapshot.FramebufferWidth : Math.Max(1, snapshot.ClientWidth);
            int height = snapshot.FramebufferHeight > 0 ? snapshot.FramebufferHeight : Math.Max(1, snapshot.ClientHeight);
            return new Vector2D<int>(width, height);
        }

        private Vector2D<int> GetCurrentWindowSize()
        {
            WindowSurfaceSnapshot snapshot = _desktopBackend?.Surface ?? default;
            return new Vector2D<int>(Math.Max(1, snapshot.ClientWidth), Math.Max(1, snapshot.ClientHeight));
        }


        private void RefreshWindowBackendOwnership(string reason)
        {
            RuntimeWindowBackendKind backendKind = _desktopBackend?.Kind ?? RuntimeWindowBackendKind.Unknown;
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


        #endregion

        #region Renderer Lifecycle

        private AbstractRenderer CreateRendererForCurrentWindow(string reason)
        {
            RuntimeGraphicsApiKind apiKind = _desktopBackend?.GraphicsApi
                ?? throw new InvalidOperationException("A renderer requires an installed desktop window backend.");
            AbstractRenderer renderer = (AbstractRenderer)RuntimeRenderingHostServices.Factories.RendererBackends.CreateRequired(
                apiKind,
                new RendererBackendCreateContext(this),
                RendererBackendCapabilities.DesktopPresentation);
            Debug.Rendering(
                "[XRWindow] Renderer created for hash={0}. RendererType={1} Api={2} Reason={3}",
                GetHashCode(),
                renderer.GetType().Name,
                apiKind,
                reason);
            return renderer;
        }

        private bool DestroyRenderer(AbstractRenderer renderer, string reason, bool waitForGpu)
        {
            Debug.Rendering(
                "[XRWindow] Destroying renderer for hash={0}. RendererType={1} WaitForGpu={2} Reason={3}",
                GetHashCode(),
                renderer.GetType().Name,
                waitForGpu,
                reason);

            if (renderer.ShouldSkipNativeWindowDisposeForShutdown)
            {
                Debug.RenderingWarning(
                    "[XRWindow] Renderer teardown skipped after shutdown abandonment. Window={0} RendererType={1} Reason={2}",
                    GetHashCode(),
                    renderer.GetType().Name,
                    reason);
                return false;
            }

            bool retirementBegan = TryRendererCleanupStep(
                renderer,
                reason,
                "BeginBackendRetirement",
                renderer.BeginBackendRetirement);
            if (!retirementBegan)
            {
                renderer.AbandonShutdownTeardown();
                return false;
            }

            if (!TryPrepareOpenXrForRendererTeardown(renderer, reason))
            {
                renderer.AbandonShutdownTeardown();
                return false;
            }

            if (waitForGpu)
            {
                bool waitCompleted = false;
                bool waitSucceeded = TryRendererCleanupStep(
                    renderer,
                    reason,
                    "WaitForGpu",
                    () => waitCompleted = renderer.TryWaitForGpu(RendererShutdownGpuWaitTimeout));
                if (!waitSucceeded || !waitCompleted)
                {
                    renderer.AbandonShutdownTeardown();
                    Debug.RenderingWarning(
                        "[XRWindow] Abandoning renderer teardown because the GPU-idle boundary failed or exceeded {0:F0} ms. " +
                        "Window={1} RendererType={2} Reason={3}",
                        RendererShutdownGpuWaitTimeout.TotalMilliseconds,
                        GetHashCode(),
                        renderer.GetType().Name,
                        reason);
                    return false;
                }
            }

            // Waiting can itself abandon native work. Recheck the policy before
            // releasing resources that the abandoned context may still reference.
            if (renderer.ShouldSkipNativeWindowDisposeForShutdown)
            {
                renderer.AbandonShutdownTeardown();
                Debug.RenderingWarning(
                    "[XRWindow] Renderer teardown abandoned during GPU drain. Window={0} RendererType={1} Reason={2}",
                    GetHashCode(), renderer.GetType().Name, reason);
                return false;
            }

            // Query pairs are renderer-owned and must be destroyed while this renderer still owns its context.
            TryRendererCleanupStep(
                renderer,
                reason,
                "CleanupOcclusionGpuElapsedTiming",
                () => OcclusionGpuElapsedTiming.Instance.CleanupRenderer(renderer));

            if (!TryRendererCleanupStep(renderer, reason,
                    "PrepareForApiObjectTeardown", renderer.PrepareForApiObjectTeardown))
            {
                renderer.AbandonShutdownTeardown();
                return false;
            }

            bool wrappersDestroyed = TryRendererCleanupStep(
                renderer,
                reason,
                "DestroyCachedAPIRenderObjects",
                renderer.DestroyCachedAPIRenderObjects);
            bool stableObjectsDestroyed = TryRendererCleanupStep(
                renderer,
                reason,
                "DestroyObjectsForRenderer",
                () => RuntimeRenderingHostServices.BackendInterop.DestroyObjectsForRenderer(renderer));
            bool cleanupCompleted = TryRendererCleanupStep(
                renderer,
                reason,
                "CleanUp",
                waitForGpu ? renderer.CleanUpAfterGpuIdle : renderer.CleanUp);
            return wrappersDestroyed && stableObjectsDestroyed && cleanupCompleted;
        }

        internal bool TryDetachRendererForReplacement(string reason, out string? failureReason)
        {
            failureReason = null;
            if (_isDisposed || _isDisposing)
            {
                failureReason = "The window is disposing.";
                return false;
            }

            if (_rendererRecreationInProgress)
            {
                failureReason = "Another renderer replacement is already in progress.";
                return false;
            }

            if (RuntimeEngine.VRState.IsInVR)
            {
                failureReason =
                    "An XR session is active. Stop OpenXR/OpenVR presentation before reloading the renderer backend.";
                return false;
            }

            _rendererRecreationInProgress = true;
            AbstractRenderer retiring = _renderer;
            if (ReferenceEquals(AbstractRenderer.Current, retiring))
                AbstractRenderer.Current = null;

            bool destroyed = DestroyRenderer(retiring, reason, waitForGpu: true);
            if (!destroyed)
            {
                _rendererRecreationInProgress = false;
                failureReason = "Renderer cleanup or GPU drain did not complete safely.";
                return false;
            }

            // Keep the retired instance assigned as an inert placeholder until the
            // replacement is ready. Collapsed render-loop dispatch uses the window's
            // initialized state and renderer presence to decide whether to keep servicing
            // render-thread jobs; clearing either here can strand the queued candidate-
            // initialization job. RenderFrame skips this interval explicitly.
            return true;
        }

        internal bool TryAttachReplacementRenderer(string reason, out string? failureReason)
            => TryAttachReplacementRenderer(reason, out failureReason, out _);

        /// <summary>
        /// Reports whether a failed candidate was completely retired. Configuration rollback
        /// must not change native loader settings while an attempted candidate remains alive.
        /// </summary>
        internal bool TryAttachReplacementRenderer(
            string reason,
            out string? failureReason,
            out bool failedCandidateCleanedUp)
        {
            failureReason = null;
            failedCandidateCleanedUp = true;
            bool attached = false;
            AbstractRenderer? replacement = null;
            AbstractRenderer previous = _renderer;
            try
            {
                replacement = CreateRendererForCurrentWindow(reason);
                replacement.Initialize();

                _renderer = replacement;
                _rendererInitialized = true;
                _renderPermanentlyDisabled = false;
                _renderPermanentlyDisabledReason = null;
                ResetRenderCircuitBreaker();
                InvalidateRendererDependentResourcesAfterRecovery();
                attached = true;
                return true;
            }
            catch (Exception ex)
            {
                _lastRenderException = ex;
                failureReason = ex.ToString();
                if (replacement is not null)
                {
                    try
                    {
                        failedCandidateCleanedUp = DestroyRenderer(
                            replacement, $"failed replacement attachment: {reason}", waitForGpu: true);
                    }
                    catch (Exception cleanupException)
                    {
                        failedCandidateCleanedUp = false;
                        failureReason += $"{Environment.NewLine}Candidate cleanup: {cleanupException}";
                    }

                    if (failedCandidateCleanedUp)
                        _renderer = previous;
                    else
                    {
                        _renderer = replacement;
                        failureReason += $"{Environment.NewLine}Candidate renderer cleanup did not complete.";
                    }
                }
                return false;
            }
            finally
            {
                if (attached)
                    _rendererRecreationInProgress = false;
            }
        }

        internal void CompleteFailedRendererReplacement()
            => _rendererRecreationInProgress = false;

        private bool TryPrepareOpenXrForRendererTeardown(AbstractRenderer renderer, string reason)
        {
            try
            {
                return RuntimeEngine.VRState.OpenXRApi?.PrepareRendererDeviceTeardown(renderer, reason) ?? true;
            }
            catch (Exception ex)
            {
                Debug.RenderingWarning(
                    "[XRWindow] OpenXR pre-renderer-teardown step failed. Window={0} RendererType={1} Reason={2} Error={3}",
                    GetHashCode(),
                    renderer.GetType().Name,
                    reason,
                    ex);
                return false;
            }
        }

        private bool TryRendererCleanupStep(AbstractRenderer renderer, string reason, string step, Action action)
        {
            try
            {
                using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope($"XRWindow.RendererCleanup.{step}");
                action();
                return true;
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
                return false;
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

            if (IsOpenXrOwnedVulkanDeviceLoss(lostRenderer))
            {
                RuntimeEngine.VRState.OpenXRApi?.PrepareRendererDeviceLossAbandonment(
                    lostRenderer,
                    reason,
                    reason.Contains("ResidentTemplateLifetimeFaultInjection", StringComparison.Ordinal)
                        ? API.Rendering.OpenXR.OpenXrDeviceLossSource.SimulatedStateMachineFault
                        : API.Rendering.OpenXR.OpenXrDeviceLossSource.ObservedNativeFault);
                DisableRenderingPermanently(
                    $"OpenXR-owned Vulkan renderer reported device loss and cannot be safely recreated in-place. Restart the editor or OpenXR runtime before launching OpenXR again. Reason: {reason}",
                    exception);
                return false;
            }

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
                _desktopBackend?.GraphicsApi.ToString() ?? "unavailable",
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

        private static bool IsOpenXrOwnedVulkanDeviceLoss(AbstractRenderer renderer)
            => ((IRuntimeRendererHost)renderer).TryGetBackendCapability<IOpenXrDeviceOwnershipBackendCapability>(out var capability) &&
               capability is not null &&
               capability.UsesOpenXrManagedDeviceCreation;

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
                    RuntimeRenderingHostServices.Factories.ReplicateWindowTargetWorldChange(this);
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
        {
            if (controller.Viewport is XRViewport existingViewport)
            {
                if (Viewports.Contains(existingViewport))
                    return existingViewport;

                controller.Viewport = null;
            }

            var existingByIndex = Viewports.FirstOrDefault(vp => vp.AssociatedPlayer?.LocalPlayerIndex == controller.LocalPlayerIndex);
            if (existingByIndex is not null)
                return existingByIndex;

            var unassigned = Viewports.FirstOrDefault(vp => vp.AssociatedPlayer is null);
            if (unassigned is not null)
                return unassigned;

            return AddViewportForPlayer(controller, autoSizeAllViewports);
        }

        /// <summary>
        /// Reorders and relayouts existing local-player viewports without replacing their
        /// runtime render pipelines or timer subscriptions.
        /// </summary>
        public void ResizeAllViewportsAccordingToPlayers()
        {
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.ResizeAllViewportsAccordingToPlayers");

            XRViewport[] orderedViewports = [.. Viewports
                .Where(x => x.AssociatedPlayer?.IsLocal == true)
                .DistinctBy(x => x.AssociatedPlayer)
                .OrderBy(x => (int)(x.AssociatedPlayer!.LocalPlayerIndex ?? 0))];

            HashSet<XRViewport> retainedViewports = [.. orderedViewports];
            XRViewport[] removedViewports = [.. Viewports.Where(x => !retainedViewports.Contains(x))];
            for (int i = 0; i < removedViewports.Length; i++)
                Viewports.Remove(removedViewports[i]);

            if (!Viewports.SequenceEqual(orderedViewports))
                Viewports.Set(orderedViewports, reportRemoved: false, reportAdded: false, reportModified: false);

            int viewportCount = orderedViewports.Length;
            ETwoPlayerPreference twoPlayerPreference = RuntimeRenderingHostServices.FrameTiming.TwoPlayerViewportPreference;
            EThreePlayerPreference threePlayerPreference = RuntimeRenderingHostServices.FrameTiming.ThreePlayerViewportPreference;
            for (int i = 0; i < viewportCount; i++)
                orderedViewports[i].ViewportCountChanged(i, viewportCount, twoPlayerPreference, threePlayerPreference);

            ResizeViewports(EffectiveWindowSize);
            RequestRenderStateRecheck();
        }

        public void UpdateViewportSizes()
        {
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.UpdateViewportSizes");
            ResizeViewports(EffectiveWindowSize);
        }

        #endregion

        #region Public Methods - Player Registration

        public void RegisterLocalPlayer(ELocalPlayerIndex playerIndex, bool autoSizeAllViewports)
        {
            IPawnController? player = RuntimePlayerControllerServices.Current?.GetOrCreateLocalPlayer(playerIndex);
            if (player is not null)
                RegisterController(player, autoSizeAllViewports);
        }

        public void RegisterController(IPawnController controller, bool autoSizeAllViewports)
            => GetOrAddViewportForPlayer(controller, autoSizeAllViewports).AssociatedPlayer = controller;

        /// <summary>
        /// Rebinds every viewport associated with <paramref name="previousController"/>
        /// to <paramref name="replacementController"/>. This preserves split-screen and
        /// multi-viewport layouts when an input integration replaces a controller type.
        /// </summary>
        public int RebindController(IPawnController previousController, IPawnController replacementController)
        {
            ArgumentNullException.ThrowIfNull(previousController);
            ArgumentNullException.ThrowIfNull(replacementController);

            int reboundCount = 0;
            foreach (XRViewport viewport in Viewports)
            {
                if (!ReferenceEquals(viewport.AssociatedPlayer, previousController))
                    continue;

                viewport.AssociatedPlayer = replacementController;
                reboundCount++;
            }

            return reboundCount;
        }

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
            IPawnController? controller = RuntimePlayerControllerServices.Current?.GetLocalPlayer(playerIndex);
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
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.RenderViewports");
            EventList<XRViewport> viewports = Viewports;
            for (int i = 0; i < viewports.Count; i++)
            {
                XRViewport viewport = viewports[i];
                using var viewportSample = RuntimeRenderingHostServices.Profiling.StartProfileScope(viewport.RenderProfileName);
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

            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.RenderViewportsToFBO");
            EventList<XRViewport> viewports = Viewports;
            for (int i = 0; i < viewports.Count; i++)
            {
                XRViewport viewport = viewports[i];
                using var viewportSample = RuntimeRenderingHostServices.Profiling.StartProfileScope(viewport.RenderToFboProfileName);
                viewport.Render(targetFBO);
            }
        }

        #endregion

        #region Window Event Handlers


        private bool HandleDesktopCloseRequested()
        {
            MarkCloseRequestedOrApproved();
            ClosingRequested?.Invoke(this);
            PublishWindowEventSnapshot(closeRequested: true, closeApproved: false);

            if (!_isDisposing && !_isDisposed && !RuntimeRenderingHostServices.Factories.AllowWindowClose(this))
            {
                ClearCloseRequestedOrApproved();
                PublishWindowEventSnapshot(closeRequested: false, closeApproved: false);
                return false;
            }

            PublishWindowEventSnapshot(closeRequested: true, closeApproved: true);
            if (!RuntimeRenderingHostServices.Factories.QuiesceForWindowRendererTeardown(this))
            {
                _renderer.AbandonShutdownTeardown();
                Debug.RenderingWarning(
                    "[XRWindow] Host work did not quiesce; retaining native window resources after renderer teardown abandonment. hash={0}",
                    GetHashCode());
            }

            // The native close callback must unwind before either renderer or window disposal.
            _approvedNativeCloseInProgress = true;
            if (!IsNativeEventPumpExternallyOwned)
            {
                // The collapsed host pump completes the close on this thread after the callback
                // unwinds. Do not queue a render-thread job here: quiescing the final window stops
                // the engine timer, and render-thread jobs drain only during a timer render dispatch.
                Interlocked.Exchange(ref _approvedNativeCloseCompletionPending, 1);
                return true;
            }

            RuntimeEngine.EnqueueRenderThreadTask(
                () => TryBeginExternalPumpDispose("DesktopClose"),
                $"XRWindow.DesktopClose[{GetHashCode()}]",
                RenderThreadJobKind.RequiresGraphicsContext);
            return true;
        }

        /// <summary>
        /// Completes an approved native close after the native close callback has unwound.
        /// </summary>
        /// <remarks>
        /// The collapsed window host calls this on the native window thread, which also owns the
        /// renderer. The completion does not depend on the engine timer, so the final window closes
        /// after its quiesce stops the timer. A native close (window button or WM_CLOSE) also
        /// completes after a terminal loop fault stopped the timer. A <see cref="RequestClose"/> call
        /// from another thread still needs a timer render dispatch to start the native close.
        /// The final-window quiesce runs again here. Engine.ShutDown can approve several closes
        /// before the first one completes. Each approval then sees more than one registered
        /// window, so no approval runs the final-window quiesce.
        /// </remarks>
        /// <returns>True when this call completed a pending close.</returns>
        private bool TryCompleteApprovedNativeClose()
        {
            // Only the native window thread sets and reads this flag; skip the locked exchange when idle.
            if (Volatile.Read(ref _approvedNativeCloseCompletionPending) == 0 ||
                Interlocked.Exchange(ref _approvedNativeCloseCompletionPending, 0) == 0)
                return false;

            try
            {
                if (!_renderer.IsShutdownTeardownAbandoned &&
                    !RuntimeRenderingHostServices.Factories.QuiesceForWindowRendererTeardown(this))
                {
                    _renderer.AbandonShutdownTeardown();
                    Debug.RenderingWarning(
                        "[XRWindow] Host work did not quiesce before approved close completion; retaining native window resources. hash={0}",
                        GetHashCode());
                }

                Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"[XRWindow] Approved native close disposal failed. hash={GetHashCode()}");
            }
            finally
            {
                RuntimeRenderingHostServices.Factories.RemoveWindow(this);
            }

            return true;
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
                QueueInteractivePresentationResize(obj, "desktop-framebuffer-callback-live");
            else
                QueueFramebufferResize(obj, "desktop-framebuffer-callback");
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
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.FramebufferResize");

            try
            {
                WarnIfNotRenderOwnerThread("ApplyFramebufferResize");

                if (_desktopBackend?.GraphicsApi == RuntimeGraphicsApiKind.OpenGL)
                    _desktopBackend.GlContext?.MakeCurrent();

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


        #endregion

        #region Window Linking

        private void LinkWindow()
        {
            // Subscribe to play mode transitions to invalidate scene panel resources
            RuntimeRenderingHostServices.Scheduling.SubscribePlayModeTransitions(OnPlayModeTransition);
            RuntimeEngine.PlayMode.PostEnterPlay += OnPlayModeTransition;
            RuntimeEngine.PlayMode.PreExitPlay += OnPlayModeTransition;
        }

        private void UnlinkWindow()
        {
            // Unsubscribe from play mode events
            RuntimeRenderingHostServices.Scheduling.UnsubscribePlayModeTransitions(OnPlayModeTransition);
            RuntimeEngine.PlayMode.PostEnterPlay -= OnPlayModeTransition;
            RuntimeEngine.PlayMode.PreExitPlay -= OnPlayModeTransition;
        }

        private void OnFileDropped(string[] paths)
            => FileDropped?.Invoke(this, paths);

        private void OnPlayModeTransition()
        {
            IRuntimeRenderFrameTimingServices frameTiming = RuntimeRenderingHostServices.FrameTiming;
            bool isTransitioning = frameTiming.IsPlayModeTransitioning;
            Debug.Rendering($"[XRWindow] OnPlayModeTransition called. PlayModeState={frameTiming.PlayModeStateName} Viewports={Viewports.Count} Transitioning={isTransitioning}");
            
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
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.VerifyTick");

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
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.BeginTick");
            ObserveRenderOwnerThread("BeginTick");
            WarnIfNotRenderOwnerThread("BeginTick");

            Debug.Rendering("[XRWindow] BeginTick hash={0} viewports={1} targetWorld={2}", GetHashCode(), Viewports.Count, TargetWorldInstance?.TargetWorldName ?? "<null>");

            _renderer.Initialize();
            _rendererInitialized = true;
            RuntimeRenderingHostServices.Scheduling.SubscribeWindowTickCallbacks(SwapBuffers, RenderFrame);

            Debug.Rendering("[XRWindow] Tick callbacks subscribed hash={0}.", GetHashCode());
        }

        private void EndTick()
        {
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.EndTick");
            WarnIfNotRenderOwnerThread("EndTick");

            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.EndTick.Unsubscribe"))
                RuntimeRenderingHostServices.Scheduling.UnsubscribeWindowTickCallbacks(SwapBuffers, RenderFrame);
            DestroyRenderer(_renderer, "EndTick", waitForGpu: true);
            _rendererInitialized = false;
        }

        private void SwapBuffers()
        {
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.SwapBuffers");
        }

        private void RenderFrame()
        {
            // Guard against rendering after window is disposed or GL context is invalid
            if (ShouldStopRenderingForClose())
                return;

            // The retiring renderer has completed GPU teardown and must never receive
            // another frame. Keeping it assigned during the transaction lets the host
            // continue servicing render-thread jobs without touching destroyed GPU state.
            if (_rendererRecreationInProgress)
                return;

            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.Timer.RenderFrame");
            ObserveRenderOwnerThread("RenderFrame");
            WarnIfNotRenderOwnerThread("RenderFrame");
            ulong renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            bool interactiveResizeFrame =
                IsInteractiveResizeInProgress ||
                RuntimeInteractiveResizeDispatchState.IsActive;

            long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ConsumeLatestWindowSurfaceSnapshotForRenderFrame();
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.ConsumeWindowSurfaceSnapshot", phaseStart);

            if (ShouldStopRenderingForClose())
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Volatile.Read(ref _interactiveResizeInProgress) == 0 ||
                Volatile.Read(ref _pendingFramebufferResize) != 0)
            {
                ProcessPendingFramebufferResize();
            }
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.ProcessPendingFramebufferResize", phaseStart);

            if (ShouldStopRenderingForClose())
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Volatile.Read(ref _interactiveResizeInProgress) != 0)
                ProcessPendingInteractivePresentationResize();
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.ProcessPendingInteractivePresentationResize", phaseStart);

            if (ShouldStopRenderingForClose())
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            {
                using var doRenderSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.Timer.DoRender");
                WarnIfNotNativeWindowThread("Window.DoRender.RenderFrame");
                try
                {
                _desktopBackend?.DispatchRender();
                }
                catch (InvalidOperationException ex) when (IsOpenGlContextUnavailableForRender(ex))
                {
                    _lastRenderException = ex;
                    MarkCloseRequestedOrApproved();
                    Debug.RenderingWarningEvery(
                        $"XRWindow.DoRender.ContextUnavailable.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[XRWindow] Suppressed render after the OpenGL context became unavailable. hash={0} message={1}",
                        GetHashCode(),
                        ex.Message);

                    if (!_isDisposed && !_isDisposing)
                        ProcessDeferredCloseRequest();
                    return;
                }
            }
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.DoRender", phaseStart);

            if (ShouldStopRenderingForClose())
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            TryCommitPendingFullInternalResizeAfterRender("render-frame");
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.CommitPendingFullInternalResize", phaseStart);

            if (ShouldStopRenderingForClose())
                return;

            phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!interactiveResizeFrame)
            {
                using var mainThreadJobsSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.Timer.PostRenderMainThreadJobs");
                // Draw the frame first, then spend a small budget on queued GPU work.
                // This keeps texture uploads and property updates from delaying visible rendering.
                AbstractRenderer postRenderRenderer = _renderer;
                if (postRenderRenderer.AcceptsBackendWork && !postRenderRenderer.IsDeviceLost)
                {
                    using var currentRendererScope = AbstractRenderer.EnterThreadCurrentScope(postRenderRenderer);
                    bool wasActive = postRenderRenderer.Active;
                    postRenderRenderer.Active = true;
                    try
                    {
                        RuntimeEngine.ProcessMainThreadTasks();
                    }
                    finally
                    {
                        postRenderRenderer.Active = wasActive;
                    }
                }
            }
            RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.PostRenderMainThreadJobs", phaseStart);

            if (ShouldStopRenderingForClose())
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

        private static void RecordWindowFrameOutput(
            EFrameOutputKind outputKind,
            EVrOutputViewKind viewKind,
            EFrameOutputPhase phase,
            string name,
            bool rendered,
            bool sceneRendered,
            bool mirror,
            bool separateSceneRender,
            bool sharedVisibility,
            long elapsedTicks)
        {
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            var pacing = FrameOutputPacingDecision.Due(viewKind, outputKind, frameId);
            RecordWindowFrameOutput(
                outputKind,
                viewKind,
                phase,
                name,
                rendered,
                sceneRendered,
                mirror,
                separateSceneRender,
                sharedVisibility,
                pacing,
                elapsedTicks);
        }

        private static void RecordWindowFrameOutput(
            EFrameOutputKind outputKind,
            EVrOutputViewKind viewKind,
            EFrameOutputPhase phase,
            string name,
            bool rendered,
            bool sceneRendered,
            bool mirror,
            bool separateSceneRender,
            bool sharedVisibility,
            in FrameOutputPacingDecision pacing,
            long elapsedTicks)
        {
            double cpuMs = elapsedTicks <= 0L
                ? 0.0
                : elapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutput(
                new FrameOutputTelemetry(
                    outputKind,
                    viewKind,
                    phase,
                    pacing,
                    name,
                    string.Empty,
                    true,
                    rendered,
                    sceneRendered,
                    mirror,
                    separateSceneRender,
                    sharedVisibility,
                    0,
                    0,
                    0,
                    0,
                    cpuMs,
                    0.0));
        }

        private static FrameOutputPacingDecision EvaluateWindowPresentPacing(bool mirrorByComposition)
        {
            IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
            EVrOutputViewKind viewKind = presentation.IsInVR && mirrorByComposition
                ? EVrOutputViewKind.CyclopeanDesktop
                : EVrOutputViewKind.DesktopEditor;
            return FrameOutputPacingDecision.Due(
                viewKind,
                EFrameOutputKind.Present,
                RuntimeEngine.Rendering.State.RenderFrameId,
                presentation.GetVrOutputTargetRateHz(viewKind));
        }

        private bool ShouldBeRendering()
            => !_isDisposed && !_isDisposing && !IsCloseRequestedOrApproved && Viewports.Count > 0 && TargetWorldInstance is not null;

        private bool ShouldStopRenderingForClose()
        {
            if (_isDisposed || _isDisposing)
                return true;

            if (!IsCloseRequestedOrApproved)
                return false;

            ProcessDeferredCloseRequest();
            return true;
        }

        private bool IsOpenGlContextUnavailableForRender(InvalidOperationException exception)
            => _desktopBackend?.GraphicsApi == RuntimeGraphicsApiKind.OpenGL &&
               exception.Message.Contains("OpenGL functions can only be used after initialization", StringComparison.Ordinal);

        private bool HasRenderableHostSurface()
        {
            Vector2D<int> framebufferSize = RenderFramebufferSize;
            Vector2D<int> windowSize = RenderWindowSize;

            int width = Math.Max(framebufferSize.X, windowSize.X);
            int height = Math.Max(framebufferSize.Y, windowSize.Y);
            return width > 0 && height > 0;
        }

        #endregion

        #region Render Callback

        //private float _lastFrameTime = 0.0f;
        private void RenderCallback(double delta)
        {
            if (_isDisposed || _isDisposing || IsCloseRequestedOrApproved)
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

            using var frameSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.RenderFrame");
            AbstractRenderer frameRenderer = _renderer;
            LatchRenderSurface();

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

                ulong renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
                bool interactiveResizeFrame =
                    IsInteractiveResizeInProgress ||
                    RuntimeInteractiveResizeDispatchState.IsActive;

                if (interactiveResizeFrame)
                {
                    // The native thread may publish another size between pending
                    // resize consumption and DoRender. Layout and Vulkan's output
                    // mapping must use this same latched surface for the whole
                    // frame, without admitting a new internal resource generation.
                    Vector2D<int> presentationSize = RenderFramebufferSize;
                    for (int viewportIndex = 0; viewportIndex < Viewports.Count; viewportIndex++)
                        Viewports[viewportIndex].SetPresentationOutputExtent(
                            (uint)presentationSize.X, (uint)presentationSize.Y);
                }

                // Statistics resolve prior-frame GPU queries through the current backend.
                frameRenderer.Active = true;
                AbstractRenderer.Current = frameRenderer;

                // Reset per-frame rendering statistics at the start of each frame.
                long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                using (var renderStatsSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.BeginRenderStatsFrame"))
                {
                    RuntimeRenderingHostServices.Statistics.BeginRenderStatsFrame();
                }
                RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.BeginRenderStatsFrame", phaseStart);

                phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!interactiveResizeFrame)
                {
                    using var gpuReadbackSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.PollGpuRenderStatsReadbacks");
                    frameRenderer.PollGpuRenderStatsReadbacks();
                }
                RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.PollGpuRenderStatsReadbacks", phaseStart);

                phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!interactiveResizeFrame)
                {
                    using var screenshotReadbackSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.PollScreenshotReadbacks");
                    frameRenderer.PollScreenshotReadbacks();
                }
                RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.PollScreenshotReadbacks", phaseStart);

                // Process any pending async buffer uploads within the frame budget.
                phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!interactiveResizeFrame &&
                    frameRenderer.AcceptsBackendWork &&
                    !frameRenderer.IsDeviceLost)
                {
                    using var uploadSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.ProcessPendingUploads");
                    using var currentRendererScope = AbstractRenderer.EnterThreadCurrentScope(frameRenderer);
                    bool wasActive = frameRenderer.Active;
                    frameRenderer.Active = true;
                    try
                    {
                        frameRenderer.ProcessPendingUploads();
                    }
                    finally
                    {
                        frameRenderer.Active = wasActive;
                    }
                }
                RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.ProcessPendingUploads", phaseStart);

                OcclusionGpuElapsedTiming.Instance.Resolve(frameRenderer, renderFrameId);

                // Publish the effective strategy and meshlet capability snapshot at the
                // renderer-current boundary. Profile capture may observe this frame before
                // any mesh pass resolves its strategy, so it must not report the default
                // startup value for the first sample.
                _ = RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();

                bool useScenePanelMode = RuntimeRenderingHostServices.Presentation.IsWindowScenePanelPresentationEnabled;
                bool forceFullViewport = RuntimeRenderingHostServices.Presentation.ForceFullViewport;
                if (forceFullViewport)
                    useScenePanelMode = false;
                EVrMirrorMode mirrorMode = RuntimeRenderingHostServices.Presentation.VrMirrorMode;
                bool mirrorByComposition =
                    RuntimeRenderingHostServices.Presentation.IsInVR &&
                    RuntimeRenderingHostServices.Presentation.IsOpenXRActive &&
                    RuntimeRenderingHostServices.Presentation.RenderWindowsWhileInVR &&
                    mirrorMode is EVrMirrorMode.BlitSubmittedEye or EVrMirrorMode.CyclopeanReconstruct;
                bool hasRenderableHostSurface = HasRenderableHostSurface();
                if (!hasRenderableHostSurface)
                {
                    Debug.RenderingEvery(
                        $"XRWindow.RenderCallback.ZeroSurface.{GetHashCode()}",
                        TimeSpan.FromMilliseconds(500),
                        "[RenderDiag] Skipping viewport rendering because the host surface is zero-sized. Window={0} WindowSize={1}x{2} FramebufferSize={3}x{4}",
                        GetHashCode(),
                        EffectiveWindowSize.X,
                        EffectiveWindowSize.Y,
                        EffectiveFramebufferSize.X,
                        EffectiveFramebufferSize.Y);
                }

                bool canRenderWindowViewports =
                    hasRenderableHostSurface &&
                    (!RuntimeRenderingHostServices.Presentation.IsInVR ||
                     (RuntimeRenderingHostServices.Presentation.RenderWindowsWhileInVR && !mirrorByComposition));

                FrameOutputPacingDecision windowPresentPacing = EvaluateWindowPresentPacing(mirrorByComposition);

                //LogRenderDiagnostics(delta, useScenePanelMode, canRenderWindowViewports, forceFullViewport);
                ApplyForcedDebugOpaquePipelineOverride();

                if (!interactiveResizeFrame)
                {
                    frameRenderer.PrepareRenderFramePacing();
                    using var preRenderSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.GlobalPreRender");
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

                using (var renderCallbackSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.RenderViewportsCallback"))
                {
                    RenderViewportsCallback?.Invoke();
                }

                // Viewport/pipeline rendering is isolated so that exceptions during scene rendering
                // do not prevent Vulkan's RenderFrameCallback (acquire/record/submit/present) from
                // executing. In OpenGL the window swap is handled by Silk.NET automatically, but
                // Vulkan requires explicit present — skipping it leaves the window uninitialized (white).
                bool viewportRenderFailed = false;
                Exception? viewportRenderException = null;
                try
                {
                    IRuntimeRenderFrameTimingServices frameTiming = RuntimeRenderingHostServices.FrameTiming;
                    if (frameTiming.IsPlayModeTransitioning)
                    {
                        Debug.RenderingEvery(
                            $"XRWindow.RenderCallback.TransitionSuspended.{GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[RenderDiag] Window viewport rendering suspended during play-mode transition. Window={0} State={1} Viewports={2}",
                            GetHashCode(),
                            frameTiming.PlayModeStateName,
                            Viewports.Count);
                    }
                    else
                    {
                        // Scoped so scene/viewport rendering cost is attributed in profiler
                        // captures instead of appearing as unexplained XRWindow.RenderFrame self time.
                        long viewportsPhaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        using (var renderViewportsSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.RenderWindowViewports"))
                        {
                            // Live resize still needs new projection and screen-space UI
                            // layout. Backend admission may defer an unready frame while
                            // the expensive internal resource generation stays frozen.
                            RenderWindowViewports(useScenePanelMode, canRenderWindowViewports, mirrorByComposition);
                        }
                        RecordRenderThreadCpuTiming(renderFrameId, "XRWindow.RenderWindowViewports", viewportsPhaseStart);
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

                if (!interactiveResizeFrame)
                {
                    using var postRenderSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.GlobalPostRender");
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

                if (RuntimeEngine.StartupPresentationEnabled && !interactiveResizeFrame)
                {
                    using var startupPresentationSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.StartupPresentationMarker");
                    Vector2D<int> framebufferSize = RenderFramebufferSize;
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
                using (var renderWindowSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.Renderer.RenderWindow"))
                {
                    long presentStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool succeeded = false;
                    try
                    {
                        frameRenderer.RenderWindow(delta);
                        succeeded = true;
                    }
                    finally
                    {
                        LastCompletedRenderWindowInterval = new XRWindowCompletedRenderInterval(
                            Interlocked.Increment(ref _completedRenderWindowIntervalSequence),
                            renderFrameId,
                            frameRenderer.BackendGeneration,
                            presentStart,
                            System.Diagnostics.Stopwatch.GetTimestamp(),
                            succeeded);
                    }
                    RecordWindowFrameOutput(
                        EFrameOutputKind.Present,
                        windowPresentPacing.ViewKind,
                        EFrameOutputPhase.Present,
                        "Window present",
                        rendered: true,
                        sceneRendered: false,
                        mirror: false,
                        separateSceneRender: false,
                        sharedVisibility: windowPresentPacing.ViewKind == EVrOutputViewKind.CyclopeanDesktop,
                        windowPresentPacing,
                        System.Diagnostics.Stopwatch.GetTimestamp() - presentStart);
                }

                using (var postViewportsSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.PostRenderViewportsCallback"))
                {
                    PostRenderViewportsCallback?.Invoke();
                }

                // Tick render-thread coroutines (e.g. progressive texture uploads) while the renderer
                // is still marked active so that IsRendererActive guards inside those coroutines pass.
                if (!interactiveResizeFrame)
                {
                    using var inFrameJobsSample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.InFrameMainThreadJobs");
                    RuntimeEngine.ProcessMainThreadTasks();
                }

                // Successful frame: clear circuit breaker state (viewport failures don't block present).
                if (!viewportRenderFailed)
                {
                    _consecutiveRenderFailures = 0;
                    _renderDisabledUntilUtc = default;
                }

                if (!interactiveResizeFrame)
                    RuntimeRenderingHostServices.Scheduling.MarkRenderFrameReadyForCollect(this);
                if (!viewportRenderFailed)
                    AnyRendererFrameCompleted?.Invoke(this, frameRenderer.BackendGeneration);
            }
            catch (Exception ex)
            {
                _lastRenderException = ex;

                // Profile captures normally keep broad file logging disabled to avoid perturbing
                // frame timings. Preserve the full exception on this cold path so a circuit-breaker
                // render stop remains diagnosable after the harness terminates the process.
                if (string.Equals(
                        Environment.GetEnvironmentVariable("XRE_PROFILE_CAPTURE"),
                        "1",
                        StringComparison.Ordinal))
                {
                    Debug.WriteAuxiliaryLog(
                        "profiler-render-exceptions.log",
                        $"[{DateTimeOffset.Now:O}] RenderFrame={RuntimeEngine.Rendering.State.RenderFrameId} Failures={_consecutiveRenderFailures + 1}\n{ex}");
                }

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
                ReleaseRenderSurfaceLatch();
                frameRenderer.Active = false;
                if (ReferenceEquals(AbstractRenderer.Current, frameRenderer))
                    AbstractRenderer.Current = null;
                Volatile.Write(ref _normalRenderActive, 0);
            }
        }

        private void ApplyForcedDebugOpaquePipelineOverride()
        {
            bool forceDebugOpaque = RuntimeRenderingHostServices.BackendInterop.ShouldForceDebugOpaquePipeline;
            if (!forceDebugOpaque)
                return;

            foreach (var viewport in Viewports)
            {
                if (viewport.RenderPipeline is DebugOpaqueRenderPipeline)
                    continue;

                viewport.RenderPipeline = RuntimeRenderingHostServices.BackendInterop.CreateDebugOpaquePipelineOverride() as RenderPipeline;
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
                TargetWorldInstance?.WorldContext.IsPlaySessionActive.ToString() ?? "<null>",
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
                    RuntimeRenderingHostServices.Presentation.IsInVR,
                    RuntimeRenderingHostServices.Presentation.RenderWindowsWhileInVR);
            }

            if (!ShouldBeRendering())
            {
                Debug.RenderingWarningEvery(
                    keyBase + ".NotRendering",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] Window not rendering: Viewports={0}, TargetWorldInstanceNull={1}, PresentationMode={2}, CanRenderWindowViewports={3}",
                    Viewports.Count,
                    TargetWorldInstance is null,
                    RuntimeRenderingHostServices.Presentation.IsWindowScenePanelPresentationEnabled,
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
                    world?.WorldContext.IsPlaySessionActive.ToString() ?? "<null>",
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
                    Vector2D<int> fb = RenderFramebufferSize;
                    uint targetWidth = (uint)Math.Max(1, fb.X);
                    uint targetHeight = (uint)Math.Max(1, fb.Y);
                    long mirrorStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool mirrorRendered = RuntimeRenderingHostServices.Presentation.TryRenderDesktopMirrorComposition(targetWidth, targetHeight);
                    if (mirrorRendered)
                        RenderDesktopMirrorUiOverlays();
                    RecordWindowFrameOutput(
                        EFrameOutputKind.DesktopMirror,
                        EVrOutputViewKind.CyclopeanDesktop,
                        EFrameOutputPhase.Render,
                        "VR desktop mirror composition",
                        rendered: mirrorRendered,
                        sceneRendered: false,
                        mirror: true,
                        separateSceneRender: false,
                        sharedVisibility: true,
                        System.Diagnostics.Stopwatch.GetTimestamp() - mirrorStart);
                }
            }
        }

        private void LatchRenderSurface()
        {
            WindowSurfaceSnapshot snapshot = LatestWindowSurfaceSnapshot;
            Vector2D<int> framebufferSize = snapshot.HasValidFramebufferExtent
                ? snapshot.FramebufferExtent
                : EffectiveFramebufferSize;
            Vector2D<int> windowSize = snapshot.HasValidClientExtent
                ? snapshot.ClientExtent
                : EffectiveWindowSize;

            Volatile.Write(ref _renderSurfaceFramebufferWidth, Math.Max(framebufferSize.X, 1));
            Volatile.Write(ref _renderSurfaceFramebufferHeight, Math.Max(framebufferSize.Y, 1));
            Volatile.Write(ref _renderSurfaceWindowWidth, Math.Max(windowSize.X, 1));
            Volatile.Write(ref _renderSurfaceWindowHeight, Math.Max(windowSize.Y, 1));
            Volatile.Write(ref _renderSurfaceLatchActive, 1);
        }

        private void ReleaseRenderSurfaceLatch()
            => Volatile.Write(ref _renderSurfaceLatchActive, 0);

        private void RenderDesktopMirrorUiOverlays()
        {
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.RenderDesktopMirrorUiOverlays");

            foreach (var viewport in Viewports)
            {
                using var viewportSample = RuntimeRenderingHostServices.Profiling.StartProfileScope($"XRViewport.RenderDesktopMirrorUiOverlay[{viewport.Index}]");
                viewport.RenderScreenSpaceUIOnly();
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
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.AddViewportForPlayer");

            XRViewport newViewport = XRViewport.ForTotalViewportCount(this, Viewports.Count);
            newViewport.AssociatedPlayer = controller;
            Viewports.Add(newViewport);
            controller?.OnPawnCameraChanged();
            newViewport.EnsureViewportBoundToCamera();

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
            using var sample = RuntimeRenderingHostServices.Profiling.StartProfileScope("XRWindow.ResizeViewports");

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

                bool skipScenePanelDispose = _approvedNativeCloseInProgress &&
                    _renderer.ShouldSkipNativeWindowDisposeForShutdown;

                if (skipScenePanelDispose)
                {
                    Debug.Rendering(
                        "[XRWindow] Fast shutdown skipping renderer-adjacent resource disposal after teardown abandonment. hash={0}",
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

                    try
                    {
                        Viewports.Clear();
                    }
                    catch
                    {
                    }
                }

            }
            finally
            {
                if (_desktopBackend?.GlContext is { } glContext &&
                    NativeWindowThreadId != Environment.CurrentManagedThreadId)
                {
                    try
                    {
                        glContext.ClearCurrent();
                    }
                    catch (Exception ex)
                    {
                        Debug.RenderingWarning(
                            "[XRWindow] Could not detach the retiring GL context before native window disposal. hash={0} error={1}",
                            GetHashCode(),
                            ex);
                        _renderer.AbandonShutdownTeardown();
                    }
                }

                RuntimeRenderingHostServices.Scheduling.EnqueueWindowThreadTask(
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

                UnlinkWindow();

                bool skipNativeWindowDispose = _renderer.ShouldSkipNativeWindowDisposeForShutdown;

                if (skipNativeWindowDispose)
                {
                    _desktopBackend?.RetainAbandonedResources();
                    Debug.Rendering(
                        "[XRWindow] Native close already approved; skipping direct native window dispose. hash={0}",
                        GetHashCode());
                }
                else
                {
                    try
                    {
                        _desktopBackend?.Dispose();
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
                    () => RuntimeRenderingHostServices.Factories.RemoveWindow(this),
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

                bool skipScenePanelDispose = _approvedNativeCloseInProgress &&
                    _renderer.ShouldSkipNativeWindowDisposeForShutdown;

                if (skipScenePanelDispose)
                {
                    Debug.Rendering(
                        "[XRWindow] Fast shutdown skipping renderer-adjacent resource disposal after teardown abandonment. hash={0}",
                        GetHashCode());
                }
                else
                {
                    // Free dockable scene-panel GPU resources.
                    _scenePanelAdapter.Dispose();
                    try
                    {
                        Viewports.Clear();
                    }
                    catch
                    {
                    }
                }

                // Unhook window events. Viewports were released only when renderer teardown was safe.
                UnlinkWindow();

                bool skipNativeWindowDispose = _renderer.ShouldSkipNativeWindowDisposeForShutdown;
                if (skipNativeWindowDispose)
                {
                    _desktopBackend?.RetainAbandonedResources();
                    Debug.Rendering(
                        "[XRWindow] Native close already approved; skipping direct native window dispose. hash={0}",
                        GetHashCode());
                }
                else
                {
                    // Finally, release the window itself if possible.
                    try
                    {
                        _desktopBackend?.Dispose();
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

    }
}

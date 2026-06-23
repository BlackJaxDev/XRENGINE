using Silk.NET.Maths;

namespace XREngine.Rendering;

public enum RuntimeWindowBackendKind
{
    Unknown,
    Glfw,
    Sdl,
    Win32,
}

[Flags]
public enum RuntimeWindowBackendOwnershipCapabilities
{
    None = 0,
    RequiresProcessMainThreadWindowing = 1 << 0,
    AllowsDedicatedWindowPump = 1 << 1,
    AllowsGraphicsContextTransfer = 1 << 2,
    SupportsSeparatePresentationThread = 1 << 3,
    SupportsWin32NativeMessages = 1 << 4,
    Experimental = 1 << 5,
}

public readonly record struct RuntimeWindowBackendOwnershipInfo(
    RuntimeWindowBackendKind BackendKind,
    RuntimeWindowBackendOwnershipCapabilities Capabilities,
    string Notes)
{
    public bool RequiresProcessMainThreadWindowing
        => Capabilities.HasFlag(RuntimeWindowBackendOwnershipCapabilities.RequiresProcessMainThreadWindowing);

    public bool AllowsDedicatedWindowPump
        => Capabilities.HasFlag(RuntimeWindowBackendOwnershipCapabilities.AllowsDedicatedWindowPump);

    public bool AllowsGraphicsContextTransfer
        => Capabilities.HasFlag(RuntimeWindowBackendOwnershipCapabilities.AllowsGraphicsContextTransfer);

    public bool SupportsSeparatePresentationThread
        => Capabilities.HasFlag(RuntimeWindowBackendOwnershipCapabilities.SupportsSeparatePresentationThread);

    public bool SupportsWin32NativeMessages
        => Capabilities.HasFlag(RuntimeWindowBackendOwnershipCapabilities.SupportsWin32NativeMessages);

    public bool IsExperimental
        => Capabilities.HasFlag(RuntimeWindowBackendOwnershipCapabilities.Experimental);

    public static RuntimeWindowBackendOwnershipInfo ForBackend(RuntimeWindowBackendKind backendKind)
        => backendKind switch
        {
            RuntimeWindowBackendKind.Glfw => new(
                backendKind,
                RuntimeWindowBackendOwnershipCapabilities.RequiresProcessMainThreadWindowing |
                RuntimeWindowBackendOwnershipCapabilities.AllowsGraphicsContextTransfer,
                "GLFW window/event APIs are process-main-thread-affine; only graphics context transfer may be attempted after explicit detach/make-current validation."),
            RuntimeWindowBackendKind.Sdl => new(
                backendKind,
                RuntimeWindowBackendOwnershipCapabilities.AllowsDedicatedWindowPump |
                RuntimeWindowBackendOwnershipCapabilities.AllowsGraphicsContextTransfer |
                RuntimeWindowBackendOwnershipCapabilities.Experimental,
                "SDL must be initialized and pumped on its owning video thread; Silk.NET wrapper behavior still needs validation before this is a default split path."),
            RuntimeWindowBackendKind.Win32 => new(
                backendKind,
                RuntimeWindowBackendOwnershipCapabilities.AllowsDedicatedWindowPump |
                RuntimeWindowBackendOwnershipCapabilities.SupportsSeparatePresentationThread |
                RuntimeWindowBackendOwnershipCapabilities.SupportsWin32NativeMessages,
                "Raw Win32 is the preferred Windows-first target for a true lightweight window pump, but it is not the active Silk.NET window backend yet."),
            _ => new(
                RuntimeWindowBackendKind.Unknown,
                RuntimeWindowBackendOwnershipCapabilities.None,
                "Unknown backend; keep window and render ownership collapsed until the backend contract is known."),
        };
}

public readonly record struct WindowSurfaceSnapshot(
    ulong Sequence,
    int ClientWidth,
    int ClientHeight,
    int FramebufferWidth,
    int FramebufferHeight,
    float DpiScaleX,
    float DpiScaleY,
    bool IsMinimized,
    bool IsInteractiveResize,
    long TimestampTicks)
{
    public Vector2D<int> ClientExtent => new(ClientWidth, ClientHeight);
    public Vector2D<int> FramebufferExtent => new(FramebufferWidth, FramebufferHeight);
    public bool HasValidClientExtent => ClientWidth > 0 && ClientHeight > 0;
    public bool HasValidFramebufferExtent => FramebufferWidth > 0 && FramebufferHeight > 0;
}

public readonly record struct WindowEventSnapshot(
    ulong Sequence,
    bool IsFocused,
    bool IsMinimized,
    bool IsCloseRequested,
    bool IsCloseApproved,
    bool IsDisposed,
    bool IsDisposing,
    long TimestampTicks,
    int PublicationThreadId)
{
    public bool IsClosingOrDisposed => IsCloseRequested || IsDisposed || IsDisposing;
}

public readonly record struct WindowInputSnapshot(
    ulong Sequence,
    int KeyboardCount,
    int MouseCount,
    int GamepadCount,
    bool IsFocused,
    bool IsMouseCaptured,
    float PointerX,
    float PointerY,
    float PointerDeltaX,
    float PointerDeltaY,
    float ScrollDeltaX,
    float ScrollDeltaY,
    uint KeyDownTransitionCount,
    uint KeyUpTransitionCount,
    uint MouseDownTransitionCount,
    uint MouseUpTransitionCount,
    uint TextInputCount,
    long TimestampTicks,
    int PublicationThreadId)
{
    public bool HasKeyboard => KeyboardCount > 0;
    public bool HasMouse => MouseCount > 0;
    public bool HasGamepad => GamepadCount > 0;
}

public readonly record struct WindowMailboxDiagnostics(
    long EnqueuedCount,
    long CompletedCount,
    long FailedCount,
    long InlineExecutionCount,
    long WrongThreadBypassCount,
    long BlockingWaitCount,
    long FlushCount,
    long FlushTimeoutCount,
    long ShutdownDrainCount,
    int CurrentDepth,
    int MaxDepth,
    double AverageQueueWaitMilliseconds,
    long LastQueueWaitTicks,
    int OwnerThreadId,
    bool IsStopping);

public readonly record struct WindowResizeExtents(
    Vector2D<int> NativeClientExtent,
    Vector2D<int> PresentationExtent,
    Vector2D<int> PipelineOutputExtent,
    Vector2D<int> FullInternalExtent,
    Vector2D<int> PendingFullInternalExtent,
    ulong FullInternalGeneration,
    ulong PendingFullInternalGeneration)
{
    public static WindowResizeExtents Empty => new(default, default, default, default, default, 0, 0);
}

public enum WindowOutputScaleMode
{
    Unknown,
    Exact,
    Upscale,
    Downscale,
    Crop,
    Letterbox,
    Pillarbox,
}

public readonly record struct WindowOutputScaleSnapshot(
    WindowOutputScaleMode Mode,
    Vector2D<int> SourceExtent,
    Vector2D<int> DestinationExtent,
    float ScaleX,
    float ScaleY)
{
    public static WindowOutputScaleSnapshot Empty => new(WindowOutputScaleMode.Unknown, default, default, 0.0f, 0.0f);
}

public readonly record struct WindowFullInternalResizePolicy(
    TimeSpan MinimumLiveGenerationInterval,
    TimeSpan MaximumLiveLag,
    float AreaRatioThreshold)
{
    public static WindowFullInternalResizePolicy Default { get; } = new(
        TimeSpan.FromMilliseconds(125),
        TimeSpan.FromMilliseconds(250),
        0.20f);
}

public sealed class WindowResizeController
{
    private readonly object _sync = new();
    private WindowSurfaceSnapshot _latestNativeSnapshot;
    private WindowResizeExtents _extents = WindowResizeExtents.Empty;
    private WindowOutputScaleSnapshot _outputScale = WindowOutputScaleSnapshot.Empty;
    private WindowFullInternalResizePolicy _policy = WindowFullInternalResizePolicy.Default;
    private ulong _lastNativeSnapshotSequence;
    private ulong _lastConsumedNativeSnapshotSequence;
    private ulong _droppedNativeSnapshotCount;
    private ulong _fullInternalGeneration;
    private ulong _pendingFullInternalGeneration;
    private long _lastFullInternalRequestTicks;

    public WindowSurfaceSnapshot LatestNativeSnapshot
    {
        get
        {
            lock (_sync)
                return _latestNativeSnapshot;
        }
    }

    public WindowResizeExtents Extents
    {
        get
        {
            lock (_sync)
                return _extents;
        }
    }

    public WindowOutputScaleSnapshot OutputScale
    {
        get
        {
            lock (_sync)
                return _outputScale;
        }
    }

    public WindowFullInternalResizePolicy Policy
    {
        get
        {
            lock (_sync)
                return _policy;
        }
    }

    public ulong LastNativeSnapshotSequence
    {
        get
        {
            lock (_sync)
                return _lastNativeSnapshotSequence;
        }
    }

    public ulong DroppedNativeSnapshotCount
    {
        get
        {
            lock (_sync)
                return _droppedNativeSnapshotCount;
        }
    }

    public ulong LastConsumedNativeSnapshotSequence
    {
        get
        {
            lock (_sync)
                return _lastConsumedNativeSnapshotSequence;
        }
    }

    public WindowResizeExtents PublishNativeSnapshot(WindowSurfaceSnapshot snapshot)
    {
        lock (_sync)
        {
            if (snapshot.Sequence <= _lastNativeSnapshotSequence)
            {
                _droppedNativeSnapshotCount++;
                return _extents;
            }

            if (_lastNativeSnapshotSequence != 0)
            {
                if (_lastConsumedNativeSnapshotSequence < _lastNativeSnapshotSequence)
                    _droppedNativeSnapshotCount++;

                if (snapshot.Sequence > _lastNativeSnapshotSequence + 1)
                    _droppedNativeSnapshotCount += snapshot.Sequence - _lastNativeSnapshotSequence - 1;
            }

            _latestNativeSnapshot = snapshot;
            _lastNativeSnapshotSequence = snapshot.Sequence;

            Vector2D<int> nativeClient = snapshot.HasValidClientExtent
                ? snapshot.ClientExtent
                : _extents.NativeClientExtent;
            Vector2D<int> fallbackPresentation = snapshot.HasValidFramebufferExtent
                ? snapshot.FramebufferExtent
                : _extents.PresentationExtent;

            _extents = new WindowResizeExtents(
                nativeClient,
                EnsureValid(_extents.PresentationExtent, fallbackPresentation),
                EnsureValid(_extents.PipelineOutputExtent, fallbackPresentation),
                EnsureValid(_extents.FullInternalExtent, fallbackPresentation),
                _extents.PendingFullInternalExtent,
                _fullInternalGeneration,
                _pendingFullInternalGeneration);
            UpdateOutputScaleNoLock();

            return _extents;
        }
    }

    public bool TryConsumeLatestNativeSnapshot(
        out WindowSurfaceSnapshot snapshot,
        out WindowResizeExtents extents)
    {
        lock (_sync)
        {
            snapshot = _latestNativeSnapshot;
            extents = _extents;
            if (snapshot.Sequence == 0 || snapshot.Sequence <= _lastConsumedNativeSnapshotSequence)
                return false;

            _lastConsumedNativeSnapshotSequence = snapshot.Sequence;
            return true;
        }
    }

    public WindowResizeExtents SetPresentationExtent(Vector2D<int> extent)
    {
        lock (_sync)
        {
            _extents = _extents with { PresentationExtent = EnsurePositive(extent) };
            UpdateOutputScaleNoLock();
            return _extents;
        }
    }

    public WindowResizeExtents SetPipelineOutputExtent(Vector2D<int> extent)
    {
        lock (_sync)
        {
            _extents = _extents with { PipelineOutputExtent = EnsurePositive(extent) };
            UpdateOutputScaleNoLock();
            return _extents;
        }
    }

    public WindowResizeExtents SetFullInternalExtent(Vector2D<int> extent)
    {
        lock (_sync)
        {
            Vector2D<int> valid = EnsurePositive(extent);
            _fullInternalGeneration++;
            if (ExtentMatches(_extents.PendingFullInternalExtent, valid))
            {
                _pendingFullInternalGeneration = 0;
                _extents = _extents with
                {
                    FullInternalExtent = valid,
                    PendingFullInternalExtent = default,
                    FullInternalGeneration = _fullInternalGeneration,
                    PendingFullInternalGeneration = 0,
                };
            }
            else
            {
                _extents = _extents with
                {
                    FullInternalExtent = valid,
                    FullInternalGeneration = _fullInternalGeneration,
                };
            }
            UpdateOutputScaleNoLock();
            return _extents;
        }
    }

    public WindowResizeExtents SetPresentationAndOutputExtent(Vector2D<int> extent)
    {
        lock (_sync)
        {
            Vector2D<int> valid = EnsurePositive(extent);
            _extents = _extents with
            {
                PresentationExtent = valid,
                PipelineOutputExtent = valid,
            };
            UpdateOutputScaleNoLock();
            return _extents;
        }
    }

    public WindowResizeExtents SetAllRenderExtents(Vector2D<int> extent)
    {
        lock (_sync)
        {
            Vector2D<int> valid = EnsurePositive(extent);
            _extents = _extents with
            {
                PresentationExtent = valid,
                PipelineOutputExtent = valid,
                FullInternalExtent = valid,
                PendingFullInternalExtent = default,
                FullInternalGeneration = ++_fullInternalGeneration,
                PendingFullInternalGeneration = 0,
            };
            _pendingFullInternalGeneration = 0;
            UpdateOutputScaleNoLock();
            return _extents;
        }
    }

    public static bool NeedsFullInternalResize(WindowSurfaceSnapshot snapshot, WindowResizeExtents extents)
        => snapshot.HasValidFramebufferExtent &&
           !ExtentMatches(extents.FullInternalExtent, snapshot.FramebufferExtent);

    public WindowResizeExtents SetPolicy(WindowFullInternalResizePolicy policy)
    {
        lock (_sync)
        {
            _policy = policy;
            return _extents;
        }
    }

    public WindowResizeExtents RequestFullInternalExtent(
        Vector2D<int> extent,
        bool force,
        long timestampTicks,
        out bool requestAccepted)
    {
        lock (_sync)
        {
            requestAccepted = false;
            Vector2D<int> valid = EnsurePositive(extent);
            if (valid.X <= 0 || valid.Y <= 0 || ExtentMatches(_extents.FullInternalExtent, valid))
            {
                if (ExtentMatches(_extents.PendingFullInternalExtent, valid))
                {
                    _pendingFullInternalGeneration = 0;
                    _extents = _extents with
                    {
                        PendingFullInternalExtent = default,
                        PendingFullInternalGeneration = 0,
                    };
                }

                return _extents;
            }

            if (!force &&
                !ShouldRequestFullInternalResizeNoLock(valid, timestampTicks) &&
                ExtentMatches(_extents.PendingFullInternalExtent, valid))
            {
                return _extents;
            }

            requestAccepted = force || ShouldRequestFullInternalResizeNoLock(valid, timestampTicks);
            if (requestAccepted)
            {
                _pendingFullInternalGeneration++;
                _lastFullInternalRequestTicks = timestampTicks;
            }

            _extents = _extents with
            {
                PendingFullInternalExtent = valid,
                PendingFullInternalGeneration = _pendingFullInternalGeneration,
            };
            return _extents;
        }
    }

    public bool IsStaleFullInternalGeneration(ulong generation)
    {
        lock (_sync)
            return generation != 0 && generation != _pendingFullInternalGeneration;
    }

    private static bool ExtentMatches(Vector2D<int> current, Vector2D<int> expected)
        => current.X == expected.X && current.Y == expected.Y;

    private static Vector2D<int> EnsureValid(Vector2D<int> current, Vector2D<int> fallback)
        => current.X > 0 && current.Y > 0 ? current : EnsurePositive(fallback);

    private static Vector2D<int> EnsurePositive(Vector2D<int> extent)
        => extent.X > 0 && extent.Y > 0
            ? extent
            : new Vector2D<int>(0, 0);

    private bool ShouldRequestFullInternalResizeNoLock(Vector2D<int> extent, long timestampTicks)
    {
        if (_lastFullInternalRequestTicks == 0)
            return true;

        if (timestampTicks > _lastFullInternalRequestTicks)
        {
            TimeSpan elapsed = TimeSpan.FromSeconds((timestampTicks - _lastFullInternalRequestTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
            if (elapsed >= _policy.MaximumLiveLag)
                return true;

            if (elapsed < _policy.MinimumLiveGenerationInterval)
                return false;
        }

        return AreaRatioDelta(_extents.FullInternalExtent, extent) >= _policy.AreaRatioThreshold;
    }

    private static float AreaRatioDelta(Vector2D<int> current, Vector2D<int> next)
    {
        long currentArea = (long)Math.Max(0, current.X) * Math.Max(0, current.Y);
        long nextArea = (long)Math.Max(0, next.X) * Math.Max(0, next.Y);
        long maxArea = Math.Max(currentArea, nextArea);
        if (maxArea <= 0)
            return 1.0f;

        return Math.Abs(nextArea - currentArea) / (float)maxArea;
    }

    private void UpdateOutputScaleNoLock()
    {
        Vector2D<int> source = EnsurePositive(_extents.FullInternalExtent);
        Vector2D<int> destination = EnsurePositive(_extents.PipelineOutputExtent);
        if (source.X <= 0 || source.Y <= 0 || destination.X <= 0 || destination.Y <= 0)
        {
            _outputScale = WindowOutputScaleSnapshot.Empty;
            return;
        }

        float scaleX = destination.X / (float)source.X;
        float scaleY = destination.Y / (float)source.Y;
        WindowOutputScaleMode mode = ResolveOutputScaleMode(source, destination, scaleX, scaleY);
        _outputScale = new WindowOutputScaleSnapshot(mode, source, destination, scaleX, scaleY);
    }

    private static WindowOutputScaleMode ResolveOutputScaleMode(
        Vector2D<int> source,
        Vector2D<int> destination,
        float scaleX,
        float scaleY)
    {
        if (source.X == destination.X && source.Y == destination.Y)
            return WindowOutputScaleMode.Exact;

        const float AspectEpsilon = 0.01f;
        float sourceAspect = source.X / (float)source.Y;
        float destinationAspect = destination.X / (float)destination.Y;
        if (MathF.Abs(sourceAspect - destinationAspect) > AspectEpsilon)
            return destinationAspect > sourceAspect
                ? WindowOutputScaleMode.Pillarbox
                : WindowOutputScaleMode.Letterbox;

        if (scaleX >= 1.0f && scaleY >= 1.0f)
            return WindowOutputScaleMode.Upscale;

        return WindowOutputScaleMode.Downscale;
    }
}

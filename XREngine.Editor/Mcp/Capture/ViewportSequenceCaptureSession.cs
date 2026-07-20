using System.Diagnostics;
using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Owns one bounded, asynchronous series of post-viewport GPU readbacks.
/// </summary>
internal sealed partial class ViewportSequenceCaptureSession
{
    private readonly object _sync = new();
    private readonly XRViewport _viewport;
    private readonly XRWindow _window;
    private readonly ViewportSequenceCaptureOptions _options;
    private readonly Action<ViewportSequenceCaptureSession> _terminalCallback;
    private readonly List<ViewportSequenceCaptureFrame> _frames;
    private readonly List<ViewportSequenceDroppedFrame> _droppedFrames = [];
    private readonly List<string> _warnings = [];
    private readonly string _backend;
    private readonly int _windowIndex;
    private readonly int _viewportIndex;
    private readonly string _windowTitle;
    private readonly string? _cameraNodeId;
    private readonly string? _cameraNodeName;
    private readonly int _initialSourceWidth;
    private readonly int _initialSourceHeight;
    private readonly int _estimatedOutputWidth;
    private readonly int _estimatedOutputHeight;
    private readonly long _startTimestamp;
    private readonly CancellationTokenSource _durationCancellation = new();

    private ViewportSequenceCaptureState _state = ViewportSequenceCaptureState.Created;
    private ViewportSequenceCaptureState _terminalOutcome = ViewportSequenceCaptureState.Completed;
    private ViewportSequenceCaptureStopReason _stopReason;
    private DateTimeOffset _startedAtUtc;
    private DateTimeOffset? _finishedAtUtc;
    private DateTimeOffset _lastUpdatedAtUtc;
    private string? _error;
    private string? _contactSheetPath;
    private int _attached;
    private int _finalizationStarted;
    private ulong _lastObservedRenderFrameId = ulong.MaxValue;
    private long _observedRenderFrameCount;
    private double _lastScheduledElapsedSeconds = double.NegativeInfinity;
    private int _scheduledFrameCount;
    private int _completedFrameCount;
    private int _failedFrameCount;
    private int _inFlightReadbacks;
    private long _inFlightReadbackBytes;
    private long _scheduledOutputPixels;

    public ViewportSequenceCaptureSession(
        Guid id,
        XRViewport viewport,
        XRWindow window,
        ViewportSequenceCaptureOptions options,
        string outputDirectory,
        int windowIndex,
        int viewportIndex,
        string backend,
        int initialSourceWidth,
        int initialSourceHeight,
        Action<ViewportSequenceCaptureSession> terminalCallback)
    {
        Id = id;
        _viewport = viewport;
        _window = window;
        _options = options;
        OutputDirectory = outputDirectory;
        ManifestPath = Path.Combine(outputDirectory, "manifest.json");
        _windowIndex = windowIndex;
        _viewportIndex = viewportIndex;
        _backend = backend;
        _windowTitle = window.WindowTitle;
        _initialSourceWidth = initialSourceWidth;
        _initialSourceHeight = initialSourceHeight;
        _estimatedOutputWidth = ScaleDimension(initialSourceWidth, options.OutputScale);
        _estimatedOutputHeight = ScaleDimension(initialSourceHeight, options.OutputScale);
        _terminalCallback = terminalCallback;
        _frames = new List<ViewportSequenceCaptureFrame>(options.CaptureLimit);
        _cameraNodeId = viewport.CameraComponent?.SceneNode?.ID.ToString("D");
        _cameraNodeName = viewport.CameraComponent?.SceneNode?.Name;
        _startedAtUtc = DateTimeOffset.UtcNow;
        _lastUpdatedAtUtc = _startedAtUtc;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    public Guid Id { get; }
    public XRViewport Viewport => _viewport;
    public string CaptureId => Id.ToString("N");
    public string OutputDirectory { get; }
    public string ManifestPath { get; }

    public DateTimeOffset StartedAtUtc
    {
        get
        {
            lock (_sync)
                return _startedAtUtc;
        }
    }

    public DateTimeOffset? FinishedAtUtc
    {
        get
        {
            lock (_sync)
                return _finishedAtUtc;
        }
    }

    public bool IsTerminal
    {
        get
        {
            lock (_sync)
                return IsTerminalState(_state);
        }
    }

    /// <summary>
    /// Attaches the session to the target window. This method must run on the main/render thread.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_state != ViewportSequenceCaptureState.Created)
                throw new InvalidOperationException($"Capture session '{CaptureId}' has already been started.");

            _state = ViewportSequenceCaptureState.Capturing;
            _lastUpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        Interlocked.Exchange(ref _attached, 1);
        try
        {
            _window.PostRenderViewportsCallback += OnPostRenderViewports;
            _window.ClosingRequested += OnWindowClosing;
        }
        catch
        {
            Detach();
            throw;
        }

        if (_options.DurationSeconds is double durationSeconds)
            _ = StopAfterDurationAsync(durationSeconds);
    }

    public bool Cancel()
    {
        lock (_sync)
        {
            if (_state is not (ViewportSequenceCaptureState.Capturing or ViewportSequenceCaptureState.Stopping))
                return false;
        }

        RequestStop(
            ViewportSequenceCaptureStopReason.UserCanceled,
            ViewportSequenceCaptureState.Canceled,
            error: null);
        return true;
    }

    public ViewportSequenceCaptureSnapshot CreateSnapshot(bool includeFrames)
    {
        lock (_sync)
            return CreateSnapshotNoLock(includeFrames);
    }

    internal static BoundingRectangle ResolveCaptureRegion(XRViewport viewport)
        => viewport.LastRenderedTargetFBO is { } targetFbo
            ? new BoundingRectangle(0, 0, (int)targetFbo.Width, (int)targetFbo.Height)
            : viewport.Region;

    private async Task StopAfterDurationAsync(double durationSeconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds), _durationCancellation.Token).ConfigureAwait(false);
            RequestStop(
                ViewportSequenceCaptureStopReason.DurationElapsed,
                ViewportSequenceCaptureState.Completed,
                error: null);
        }
        catch (OperationCanceledException)
        {
            // A frame-count stop, explicit cancellation, failure, or disposal ended the timer.
        }
    }

    private void OnWindowClosing(XRWindow _)
        => RequestStop(
            ViewportSequenceCaptureStopReason.WindowClosing,
            ViewportSequenceCaptureState.Canceled,
            "The target window began closing before the capture completed.");
}

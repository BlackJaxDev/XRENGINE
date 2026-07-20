using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

/// <summary>
/// Coordinates active and recently completed viewport sequence capture sessions.
/// </summary>
internal sealed class ViewportSequenceCaptureManager
{
    private static readonly TimeSpan CompletedSessionRetention = TimeSpan.FromHours(1);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ViewportSequenceCaptureSession> _sessions = [];

    private ViewportSequenceCaptureManager()
    {
    }

    public static ViewportSequenceCaptureManager Instance { get; } = new();

    public bool TryStart(
        XRViewport viewport,
        ViewportSequenceCaptureOptions options,
        out ViewportSequenceCaptureSession? session,
        out string? error)
    {
        session = null;
        error = null;

        XRWindow? window = viewport.Window
            ?? Engine.Windows.FirstOrDefault(candidate => candidate.Viewports.Contains(viewport));
        if (window is null || window.IsDisposed)
        {
            error = "No live window owns the target viewport.";
            return false;
        }

        AbstractRenderer? renderer = window.Renderer;

        BoundingRectangle captureRegion = ViewportSequenceCaptureSession.ResolveCaptureRegion(viewport);
        if (captureRegion.Width <= 0 || captureRegion.Height <= 0)
        {
            error = $"The target viewport has an invalid capture region of {captureRegion.Width}x{captureRegion.Height}.";
            return false;
        }

        int outputWidth = Math.Max(1, (int)Math.Round(captureRegion.Width * options.OutputScale));
        int outputHeight = Math.Max(1, (int)Math.Round(captureRegion.Height * options.OutputScale));
        long estimatedOutputPixels = (long)outputWidth * outputHeight * options.CaptureLimit;
        if (estimatedOutputPixels > ViewportSequenceCaptureOptions.MaximumTotalOutputPixels)
        {
            error = $"The requested capture would produce approximately {estimatedOutputPixels:N0} pixels, exceeding the {ViewportSequenceCaptureOptions.MaximumTotalOutputPixels:N0}-pixel session budget. Reduce frame count, max_frames, or output_scale.";
            return false;
        }

        long maximumQueuedReadbackBytes = checked((long)captureRegion.Width * captureRegion.Height * 4L * options.MaxInFlightReadbacks);
        if (maximumQueuedReadbackBytes > ViewportSequenceCaptureOptions.MaximumInFlightReadbackBytes)
        {
            error = $"The requested readback queue could retain approximately {maximumQueuedReadbackBytes / (1024.0 * 1024.0):F1} MiB, exceeding the {ViewportSequenceCaptureOptions.MaximumInFlightReadbackBytes / (1024 * 1024)} MiB limit. Reduce max_in_flight_readbacks or viewport resolution.";
            return false;
        }

        Guid id = Guid.NewGuid();
        string outputDirectory = Path.Combine(
            options.OutputRootDirectory,
            $"ViewportSequence_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{id:N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex)
        {
            error = $"Failed to create capture output directory '{outputDirectory}': {ex.Message}";
            return false;
        }

        int windowIndex = FindIndex(Engine.Windows, window);
        int viewportIndex = FindIndex(window.Viewports, viewport);
        string backend = renderer?.GetType().Name ?? "Unknown";
        var createdSession = new ViewportSequenceCaptureSession(
            id,
            viewport,
            window,
            options,
            outputDirectory,
            windowIndex,
            viewportIndex,
            backend,
            captureRegion.Width,
            captureRegion.Height,
            OnSessionTerminal);

        lock (_sync)
        {
            PruneNoLock();
            if (_sessions.Values.Any(candidate => !candidate.IsTerminal && ReferenceEquals(candidate.Viewport, viewport)))
            {
                error = "The target viewport already has an active sequence capture. Cancel or wait for it before starting another.";
                TryDeleteEmptyDirectory(outputDirectory);
                return false;
            }

            _sessions.Add(id, createdSession);
        }

        try
        {
            createdSession.Start();
            session = createdSession;
            return true;
        }
        catch (Exception ex)
        {
            lock (_sync)
                _sessions.Remove(id);

            TryDeleteEmptyDirectory(outputDirectory);
            error = $"Failed to start viewport sequence capture: {ex.Message}";
            return false;
        }
    }

    public bool TryGet(string captureId, out ViewportSequenceCaptureSession? session)
    {
        session = null;
        if (!Guid.TryParse(captureId, out Guid id))
            return false;

        lock (_sync)
        {
            PruneNoLock();
            return _sessions.TryGetValue(id, out session);
        }
    }

    public ViewportSequenceCaptureSnapshot[] List(bool activeOnly)
    {
        ViewportSequenceCaptureSession[] sessions;
        lock (_sync)
        {
            PruneNoLock();
            sessions = _sessions.Values
                .Where(session => !activeOnly || !session.IsTerminal)
                .OrderByDescending(static session => session.StartedAtUtc)
                .ToArray();
        }

        return sessions.Select(static session => session.CreateSnapshot(includeFrames: false)).ToArray();
    }

    private void OnSessionTerminal(ViewportSequenceCaptureSession _)
    {
        lock (_sync)
            PruneNoLock();
    }

    private void PruneNoLock()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - CompletedSessionRetention;
        Guid[] expired = _sessions
            .Where(static pair => pair.Value.IsTerminal)
            .Where(pair => pair.Value.FinishedAtUtc is DateTimeOffset finished && finished < cutoff)
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (Guid id in expired)
            _sessions.Remove(id);

        int excess = _sessions.Count - ViewportSequenceCaptureOptions.MaximumRetainedSessions;
        if (excess <= 0)
            return;

        Guid[] oldestTerminal = _sessions
            .Where(static pair => pair.Value.IsTerminal)
            .OrderBy(pair => pair.Value.FinishedAtUtc ?? DateTimeOffset.MaxValue)
            .Take(excess)
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (Guid id in oldestTerminal)
            _sessions.Remove(id);
    }

    private static int FindIndex<T>(IEnumerable<T> values, T target) where T : class
    {
        int index = 0;
        foreach (T value in values)
        {
            if (ReferenceEquals(value, target))
                return index;
            index++;
        }

        return -1;
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only; no capture artifacts existed yet.
        }
    }
}

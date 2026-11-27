using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.Editor.WindowRecording;

/// <summary>
/// Owns editor window MP4 recording across interactive (<c>Shift+F12</c>), startup environment
/// (<see cref="XREngineEnvironmentVariables.RecordVideoOnStartup"/>), and MCP/programmatic
/// callers. Supports both OS Windows Graphics Capture (<c>gfxcapture</c>) and in-process
/// renderer GPU readback piped to ffmpeg.
/// </summary>
public static class EditorWindowVideoRecorder
{
    /// <summary>The time that a failure message stays in the window title.</summary>
    private static readonly TimeSpan FailureTitleDuration = TimeSpan.FromSeconds(5);

    private static readonly object Sync = new();
    private static IEditorWindowVideoSession? _active;
    private static EditorWindowRecordingStatus? _lastCompletedStatus;
    private static EditorWindowRecordingOptions? _startupOptions;
    private static int _startupRecordingArmed;
    private static bool _transitionInProgress;
    private static bool _initialized;

    /// <summary>Gets a value that is true while a recording runs.</summary>
    public static bool IsRecording
    {
        get
        {
            lock (Sync)
                return _active is not null;
        }
    }

    /// <summary>
    /// Subscribes the <c>Shift+F12</c> shortcut to native window key input and arms optional
    /// startup auto-recording when <see cref="XREngineEnvironmentVariables.RecordVideoOnStartup"/>
    /// is configured. Call once at editor startup.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;

        XRWindow.AnyWindowKeyDown += OnAnyWindowKeyDown;
        _startupOptions = EditorWindowRecordingOptions.FromStartupEnvironment();
        if (_startupOptions is not null)
        {
            Interlocked.Exchange(ref _startupRecordingArmed, 1);
            XRWindow.AnyRendererFrameCompleted += OnAnyRendererFrameCompleted;
            // Pre-warm the ffmpeg locator and NVENC probe off the render thread so startup
            // recording attaches immediately on the first completed frame with no probe stall.
            _ = Task.Run(() =>
            {
                string? ffmpeg = EditorVideoFfmpegLocator.TryResolveForRawPipe(out _);
                if (ffmpeg is not null)
                    _ = EditorVideoFfmpegLocator.SupportsNvenc(ffmpeg);
            });
            Debug.Out(
                $"[WindowRecording] Armed startup video recording to '{_startupOptions.OutputPath}' " +
                $"(backend={_startupOptions.Backend}, fps={_startupOptions.FramesPerSecond}, " +
                $"max={_startupOptions.MaxWidth}x{_startupOptions.MaxHeight}, crf={_startupOptions.Crf}, " +
                $"maxBitrate={_startupOptions.MaxBitrate}).");
        }

        _initialized = true;
    }

    /// <summary>
    /// Removes hooks and stops an active recording so ffmpeg writes a playable MP4 before the
    /// editor exits.
    /// </summary>
    public static void Shutdown()
    {
        if (!_initialized)
            return;

        XRWindow.AnyWindowKeyDown -= OnAnyWindowKeyDown;
        XRWindow.AnyRendererFrameCompleted -= OnAnyRendererFrameCompleted;
        Interlocked.Exchange(ref _startupRecordingArmed, 0);
        _startupOptions = null;
        _initialized = false;
        StopActive(out _, out _);
    }

    /// <summary>
    /// Returns true when <paramref name="key"/> with the given modifier state is the recording
    /// shortcut. <c>F12</c> without Shift stays the screenshot key.
    /// </summary>
    internal static bool IsToggleShortcut(EKey key, bool leftShiftDown, bool rightShiftDown)
        => key == EKey.F12 && (leftShiftDown || rightShiftDown);

    /// <summary>
    /// Starts a recording of <paramref name="window"/> when none runs, and otherwise stops the
    /// active recording. A toggle that arrives while a start or stop is still in progress is
    /// ignored.
    /// </summary>
    public static void Toggle(XRWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (Sync)
        {
            if (_transitionInProgress)
                return;
            _transitionInProgress = true;
        }

        try
        {
            if (IsRecording)
                StopActive(out _, out _);
            else
                StartCore(window, new EditorWindowRecordingOptions(), reportFailureToWindowTitle: true, out _);
        }
        finally
        {
            lock (Sync)
                _transitionInProgress = false;
        }
    }

    /// <summary>
    /// Starts a recording of <paramref name="window"/> using <paramref name="options"/>.
    /// Returns false with a diagnostic message if a recording is already active or the
    /// requested backend fails to start.
    /// </summary>
    public static bool TryStart(XRWindow window, EditorWindowRecordingOptions? options, out EditorWindowRecordingStatus? status, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (Sync)
        {
            if (_transitionInProgress)
            {
                status = null;
                failure = "A window recording transition is already in progress.";
                return false;
            }
            if (_active is not null)
            {
                status = BuildStatus(_active, isRecording: true, lastError: null);
                failure = $"A recording is already active ('{_active.OutputPath}'). Stop it before starting another.";
                return false;
            }
            _transitionInProgress = true;
        }

        try
        {
            bool started = StartCore(window, options ?? new EditorWindowRecordingOptions(), reportFailureToWindowTitle: false, out failure);
            status = started ? GetStatus() : null;
            return started;
        }
        finally
        {
            lock (Sync)
                _transitionInProgress = false;
        }
    }

    /// <summary>
    /// Stops the active recording, flushes the MP4 file, and returns its final status.
    /// </summary>
    public static bool TryStop(out EditorWindowRecordingStatus? status, out string? failure)
    {
        lock (Sync)
        {
            if (_transitionInProgress)
            {
                status = null;
                failure = "A window recording transition is already in progress.";
                return false;
            }
            if (_active is null)
            {
                status = _lastCompletedStatus;
                failure = "No editor window recording is active.";
                return false;
            }
            _transitionInProgress = true;
        }

        try
        {
            return StopActive(out status, out failure);
        }
        finally
        {
            lock (Sync)
                _transitionInProgress = false;
        }
    }

    /// <summary>
    /// Returns the status of the currently active recording, or the last completed recording
    /// when idle.
    /// </summary>
    public static EditorWindowRecordingStatus GetStatus()
    {
        lock (Sync)
        {
            if (_active is not null)
                return BuildStatus(_active, isRecording: true, lastError: null);
            if (_lastCompletedStatus is not null)
                return _lastCompletedStatus;
        }

        return new EditorWindowRecordingStatus(
            IsRecording: false,
            OutputPath: null,
            Backend: "None",
            ElapsedSeconds: 0.0,
            CapturedFrameCount: 0,
            WrittenVideoFrameCount: 0,
            DroppedReadbackCount: 0,
            FileSizeBytes: 0,
            FramesPerSecond: EditorWindowRecordingOptions.DefaultFramesPerSecond,
            MaxWidth: EditorWindowRecordingOptions.DefaultMaxWidth,
            MaxHeight: EditorWindowRecordingOptions.DefaultMaxHeight,
            LastError: null);
    }

    private static void OnAnyWindowKeyDown(XRWindow window, EKey key)
    {
        if (!IsToggleShortcut(key, window.IsKeyPressed(EKey.ShiftLeft), window.IsKeyPressed(EKey.ShiftRight)))
            return;

        // This event runs on the native window thread. Starting ffmpeg probes executables and
        // stopping waits for the MP4 to finish, so do both on the thread pool.
        _ = Task.Run(() => Toggle(window));
    }

    private static void OnAnyRendererFrameCompleted(XRWindow window, long backendGeneration)
    {
        if (Volatile.Read(ref _startupRecordingArmed) == 0)
            return;
        if (window.IsDisposed)
            return;

        // Arm once for the very first completed editor frame so startup recording begins
        // immediately without blocking the render thread.
        if (Interlocked.Exchange(ref _startupRecordingArmed, 0) != 1)
            return;

        XRWindow.AnyRendererFrameCompleted -= OnAnyRendererFrameCompleted;
        EditorWindowRecordingOptions? options = _startupOptions;
        if (options is null)
            return;

        if (options.Backend == EEditorWindowRecordingBackend.RendererReadback)
        {
            // Starting the readback session directly subscribes PostRenderViewportsCallback
            // immediately without spawning ffmpeg on the render thread (ffmpeg starts on the
            // writer thread when the first readback arrives).
            if (!TryStart(window, options, out _, out string? failure))
                Debug.LogWarning($"[WindowRecording] Startup recording failed to start: {failure}");
            return;
        }

        _ = Task.Run(() =>
        {
            if (!TryStart(window, options, out _, out string? failure))
                Debug.LogWarning($"[WindowRecording] Startup recording failed to start: {failure}");
        });
    }

    private static bool StartCore(
        XRWindow window,
        EditorWindowRecordingOptions rawOptions,
        bool reportFailureToWindowTitle,
        out string? failure)
    {
        EditorWindowRecordingOptions options = rawOptions.Normalize();
        string outputPath = options.OutputPath ?? CreateOutputPath();
        IEditorWindowVideoSession? session = null;

        switch (options.Backend)
        {
            case EEditorWindowRecordingBackend.WindowsGraphicsCapture:
                session = TryStartWgc(window, outputPath, options, out failure);
                break;

            case EEditorWindowRecordingBackend.RendererReadback:
                session = TryStartReadback(window, outputPath, options, out failure);
                break;

            default:
                session = TryStartWgc(window, outputPath, options, out string? wgcFailure);
                if (session is null)
                {
                    Debug.LogWarning(
                        $"[WindowRecording] Windows Graphics Capture unavailable ({wgcFailure}); " +
                        "falling back to in-process renderer GPU readback.");
                    session = TryStartReadback(window, outputPath, options, out string? readbackFailure);
                    failure = session is null
                        ? $"WGC failed ({wgcFailure}); renderer readback fallback also failed ({readbackFailure})."
                        : null;
                }
                else
                {
                    failure = null;
                }
                break;
        }

        if (session is null)
        {
            string details = $"Cannot start the window recording to '{outputPath}'. {failure}";
            if (reportFailureToWindowTitle)
                ReportFailure(window, "recording failed", details);
            else
                Debug.LogWarning($"[WindowRecording] {details}");
            return false;
        }

        lock (Sync)
            _active = session;

        session.Failed += OnSessionFailed;
        session.Completed += OnSessionCompleted;
        session.Begin();
        Debug.Out(
            $"[WindowRecording] Recording window '{session.BaseTitle}' via {session.Backend} " +
            $"at {options.FramesPerSecond} fps (max {options.MaxWidth}x{options.MaxHeight}, crf={options.Crf}, " +
            $"maxBitrate={options.MaxBitrate}) to '{outputPath}'.");
        return true;
    }

    private static IEditorWindowVideoSession? TryStartWgc(
        XRWindow window,
        string outputPath,
        EditorWindowRecordingOptions options,
        out string? failure)
    {
        string? ffmpegPath = EditorVideoFfmpegLocator.TryResolve(out string? locateFailure);
        if (ffmpegPath is null)
        {
            failure = locateFailure;
            return null;
        }

        return EditorWindowVideoSession.TryStart(window, ffmpegPath, outputPath, out failure, options);
    }

    private static IEditorWindowVideoSession? TryStartReadback(
        XRWindow window,
        string outputPath,
        EditorWindowRecordingOptions options,
        out string? failure)
    {
        string? ffmpegPath = EditorVideoFfmpegLocator.TryResolveForRawPipe(out string? locateFailure);
        if (ffmpegPath is null)
        {
            failure = locateFailure;
            return null;
        }

        return EditorWindowReadbackVideoSession.TryStart(window, ffmpegPath, outputPath, options, out failure);
    }

    private static bool StopActive(out EditorWindowRecordingStatus? status, out string? failure)
    {
        IEditorWindowVideoSession? session;
        lock (Sync)
        {
            session = _active;
            _active = null;
        }

        if (session is null)
        {
            status = null;
            failure = null;
            return false;
        }

        session.Failed -= OnSessionFailed;
        session.Completed -= OnSessionCompleted;
        bool ok = session.Stop(out failure);
        status = BuildStatus(session, isRecording: false, lastError: failure);
        lock (Sync)
            _lastCompletedStatus = status;

        if (ok)
        {
            Debug.Out(
                $"[WindowRecording] Saved {EditorWindowVideoSession.FormatElapsed(session.Elapsed)} of video " +
                $"({status.FileSizeBytes / (1024.0 * 1024.0):0.0} MB, backend={session.Backend}, " +
                $"captured={status.CapturedFrameCount}, written={status.WrittenVideoFrameCount}, " +
                $"dropped={status.DroppedReadbackCount}) to '{session.OutputPath}'.");
            return true;
        }

        if (failure is not null)
            ReportFailure(session.Window, "recording failed", $"The window recording to '{session.OutputPath}' did not finish correctly. {failure}", session.BaseTitle);
        return false;
    }

    private static void OnSessionCompleted(IEditorWindowVideoSession session)
    {
        lock (Sync)
        {
            if (!ReferenceEquals(_active, session) || _transitionInProgress)
                return;
            _transitionInProgress = true;
        }

        try
        {
            StopActive(out _, out _);
        }
        finally
        {
            lock (Sync)
                _transitionInProgress = false;
        }
    }

    private static void OnSessionFailed(IEditorWindowVideoSession session, string message)
    {
        lock (Sync)
        {
            if (!ReferenceEquals(_active, session))
                return;
            _active = null;
            _lastCompletedStatus = BuildStatus(session, isRecording: false, lastError: message);
        }

        ReportFailure(session.Window, "recording stopped", $"The window recording to '{session.OutputPath}' stopped unexpectedly. {message}", session.BaseTitle);
    }

    private static EditorWindowRecordingStatus BuildStatus(IEditorWindowVideoSession session, bool isRecording, string? lastError)
    {
        long fileSize = 0;
        if (!isRecording && File.Exists(session.OutputPath))
        {
            try { fileSize = new FileInfo(session.OutputPath).Length; } catch (Exception) { }
        }

        return new EditorWindowRecordingStatus(
            IsRecording: isRecording,
            OutputPath: session.OutputPath,
            Backend: session.Backend.ToString(),
            ElapsedSeconds: Math.Round(session.Elapsed.TotalSeconds, 3),
            CapturedFrameCount: session.CapturedFrameCount,
            WrittenVideoFrameCount: session.WrittenVideoFrameCount,
            DroppedReadbackCount: session.DroppedReadbackCount,
            FileSizeBytes: fileSize,
            FramesPerSecond: session.Options.FramesPerSecond,
            MaxWidth: session.Options.MaxWidth,
            MaxHeight: session.Options.MaxHeight,
            LastError: lastError);
    }

    /// <summary>
    /// Writes <paramref name="details"/> to the log and shows <paramref name="shortMessage"/>
    /// in the window title for a few seconds, because the title is the only place where the
    /// recorder shows status.
    /// </summary>
    private static void ReportFailure(XRWindow window, string shortMessage, string details, string? baseTitle = null)
    {
        Debug.LogWarning($"[WindowRecording] {details}");

        string title = baseTitle ?? window.WindowTitle;
        window.RequestTitle(string.IsNullOrEmpty(title) ? $"REC: {shortMessage}" : $"{title}  REC: {shortMessage}");
        _ = Task.Delay(FailureTitleDuration).ContinueWith(_ =>
        {
            // Do not overwrite the title of a recording that started in the meantime.
            if (!IsRecording)
                window.RequestTitle(title);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Returns a new time-stamped MP4 path in <c>Videos/XRENGINE</c>. The desktop is the
    /// fallback when the user profile has no Videos folder.
    /// </summary>
    private static string CreateOutputPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        string directory = Path.Combine(root, "XRENGINE");
        string fileName = $"XRENGINE_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
        return Path.Combine(directory, fileName);
    }
}

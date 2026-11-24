using XREngine.Input.Devices;
using XREngine.Rendering;

namespace XREngine.Editor.WindowRecording;

/// <summary>
/// Owns the editor's global video recording shortcut. <c>Shift+F12</c> in any editor window
/// starts an MP4 recording of that window, and the next <c>Shift+F12</c> stops it. The
/// shortcut is registered on native window input, like the play mode shortcuts, so it works
/// when a game mode owns input or no pawn is possessed. The only recording status is the
/// window title, which shows <c>● REC m:ss</c> while ffmpeg runs. A failed start or stop
/// shows a short message in the title for a few seconds and writes the details to the log.
/// </summary>
public static class EditorWindowVideoRecorder
{
    /// <summary>The time that a failure message stays in the window title.</summary>
    private static readonly TimeSpan FailureTitleDuration = TimeSpan.FromSeconds(5);

    private static readonly object Sync = new();
    private static EditorWindowVideoSession? _active;
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

    /// <summary>Subscribes the shortcut to native window key input. Call it once at editor startup.</summary>
    public static void Initialize()
    {
        if (_initialized)
            return;

        XRWindow.AnyWindowKeyDown += OnAnyWindowKeyDown;
        _initialized = true;
    }

    /// <summary>
    /// Removes the shortcut and stops an active recording, so that ffmpeg writes a playable
    /// MP4 before the editor exits.
    /// </summary>
    public static void Shutdown()
    {
        if (!_initialized)
            return;

        XRWindow.AnyWindowKeyDown -= OnAnyWindowKeyDown;
        _initialized = false;
        StopActive();
    }

    /// <summary>
    /// Returns true when <paramref name="key"/> with the given modifier state is the recording
    /// shortcut. <c>F12</c> without Shift stays the screenshot key.
    /// </summary>
    internal static bool IsToggleShortcut(EKey key, bool leftShiftDown, bool rightShiftDown)
        => key == EKey.F12 && (leftShiftDown || rightShiftDown);

    /// <summary>
    /// Starts a recording of <paramref name="window"/> when none runs, and otherwise stops the
    /// active recording. A stop always applies to the active recording, also when it records
    /// a different window. A toggle that arrives while a start or stop is still in progress
    /// is ignored.
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
                StopActive();
            else
                Start(window);
        }
        finally
        {
            lock (Sync)
                _transitionInProgress = false;
        }
    }

    private static void OnAnyWindowKeyDown(XRWindow window, EKey key)
    {
        if (!IsToggleShortcut(key, window.IsKeyPressed(EKey.ShiftLeft), window.IsKeyPressed(EKey.ShiftRight)))
            return;

        // This event runs on the native window thread. Starting ffmpeg probes executables and
        // stopping waits for the MP4 to finish, so do both on the thread pool.
        _ = Task.Run(() => Toggle(window));
    }

    private static void Start(XRWindow window)
    {
        string? ffmpegPath = EditorVideoFfmpegLocator.TryResolve(out string? locateFailure);
        if (ffmpegPath is null)
        {
            ReportFailure(window, "ffmpeg not found", $"Cannot start the window recording. {locateFailure}");
            return;
        }

        string outputPath = CreateOutputPath();
        EditorWindowVideoSession? session = EditorWindowVideoSession.TryStart(window, ffmpegPath, outputPath, out string? startFailure);
        if (session is null)
        {
            ReportFailure(window, "recording failed", $"Cannot start the window recording to '{outputPath}'. {startFailure}");
            return;
        }

        lock (Sync)
            _active = session;

        session.Failed += OnSessionFailed;
        session.Begin();
        Debug.Out($"[WindowRecording] Recording window '{session.BaseTitle}' at {EditorWindowVideoSession.FramesPerSecond} fps to '{outputPath}' with '{ffmpegPath}'.");
    }

    private static void StopActive()
    {
        EditorWindowVideoSession? session;
        lock (Sync)
        {
            session = _active;
            _active = null;
        }

        if (session is null)
            return;

        session.Failed -= OnSessionFailed;
        TimeSpan elapsed = session.Elapsed;
        if (session.Stop(out string? failure))
        {
            long bytes = new FileInfo(session.OutputPath).Length;
            Debug.Out($"[WindowRecording] Saved {EditorWindowVideoSession.FormatElapsed(elapsed)} of video ({bytes / (1024.0 * 1024.0):0.0} MB) to '{session.OutputPath}'.");
            return;
        }

        if (failure is not null)
            ReportFailure(session.Window, "recording failed", $"The window recording to '{session.OutputPath}' did not finish correctly. {failure}", session.BaseTitle);
    }

    private static void OnSessionFailed(EditorWindowVideoSession session, string message)
    {
        lock (Sync)
        {
            if (!ReferenceEquals(_active, session))
                return;
            _active = null;
        }

        ReportFailure(session.Window, "recording stopped", $"The window recording to '{session.OutputPath}' stopped unexpectedly. {message}", session.BaseTitle);
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

using System.Diagnostics;
using System.Globalization;
using XREngine.Rendering;

namespace XREngine.Editor.WindowRecording;

/// <summary>
/// One ffmpeg process that records one editor window to an MP4 file. ffmpeg reads the window
/// through Windows Graphics Capture (the <c>gfxcapture</c> filter), so the engine does no GPU
/// readback and the render loop pays no capture cost. While the process runs, a timer writes
/// the elapsed time into the window title. <see cref="Stop"/> sends <c>q</c> to ffmpeg, which
/// then writes the MP4 index and exits.
/// </summary>
internal sealed class EditorWindowVideoSession : IEditorWindowVideoSession
{
    /// <summary>The default output frame rate.</summary>
    public const int FramesPerSecond = EditorWindowRecordingOptions.DefaultFramesPerSecond;

    /// <summary>The default largest output width. Larger windows are scaled down with the same aspect ratio.</summary>
    public const int MaxWidth = EditorWindowRecordingOptions.DefaultMaxWidth;

    /// <summary>The default largest output height. Larger windows are scaled down with the same aspect ratio.</summary>
    public const int MaxHeight = EditorWindowRecordingOptions.DefaultMaxHeight;

    /// <summary>The number of ffmpeg stderr lines that a failure message holds.</summary>
    private const int StderrLineCount = 12;

    /// <summary>The time that a start must survive before the session counts as recording.</summary>
    private static readonly TimeSpan StartupProbe = TimeSpan.FromMilliseconds(750);

    /// <summary>The longest time to wait for ffmpeg to finish the MP4 after it receives <c>q</c>.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(15);

    /// <summary>The interval of the window title update. Shorter than one second so that the seconds never skip.</summary>
    private static readonly TimeSpan TitleInterval = TimeSpan.FromMilliseconds(250);

    private readonly XRWindow _window;
    private readonly Process _process;
    private readonly Stopwatch _elapsed;
    private readonly Queue<string> _stderrTail = new(StderrLineCount);
    private Timer? _titleTimer;
    private long _lastTitleSecond = -1;
    private int _stopRequested;
    private int _autoStopTriggered;

    private EditorWindowVideoSession(
        XRWindow window,
        Process process,
        string outputPath,
        string baseTitle,
        EditorWindowRecordingOptions options)
    {
        _window = window;
        _process = process;
        OutputPath = outputPath;
        BaseTitle = baseTitle;
        Options = options;
        _elapsed = Stopwatch.StartNew();
    }

    /// <summary>Gets the MP4 file that this session writes.</summary>
    public string OutputPath { get; }

    /// <summary>Gets the window title from before the recording. <see cref="Stop"/> puts it back.</summary>
    public string BaseTitle { get; }

    /// <summary>Gets the recorded window.</summary>
    public XRWindow Window => _window;

    /// <inheritdoc/>
    public EEditorWindowRecordingBackend Backend => EEditorWindowRecordingBackend.WindowsGraphicsCapture;

    /// <inheritdoc/>
    public EditorWindowRecordingOptions Options { get; }

    /// <summary>Gets the time since ffmpeg started.</summary>
    public TimeSpan Elapsed => _elapsed.Elapsed;

    /// <inheritdoc/>
    public long CapturedFrameCount => 0;

    /// <inheritdoc/>
    public long WrittenVideoFrameCount => 0;

    /// <inheritdoc/>
    public long DroppedReadbackCount => 0;

    /// <summary>
    /// Raised on a thread-pool thread when ffmpeg exits after <see cref="Begin"/> and without
    /// a <see cref="Stop"/> call. The argument is a message with the exit code and the last
    /// stderr lines.
    /// </summary>
    public event Action<IEditorWindowVideoSession, string>? Failed;

    /// <inheritdoc/>
    public event Action<IEditorWindowVideoSession>? Completed;

    /// <summary>
    /// Starts ffmpeg for <paramref name="window"/> and returns the running session. Returns
    /// null and a message when the window has no native handle or ffmpeg stops at once, for
    /// example because the window is minimized or the capture filter is not available.
    /// Subscribe to <see cref="Failed"/>, then call <see cref="Begin"/> to start the title
    /// timer and the exit watch.
    /// </summary>
    public static EditorWindowVideoSession? TryStart(
        XRWindow window,
        string ffmpegPath,
        string outputPath,
        out string? failure,
        EditorWindowRecordingOptions? options = null)
    {
        EditorWindowRecordingOptions resolvedOptions = (options ?? new EditorWindowRecordingOptions()).Normalize();
        nint windowHandle = window.OperatingSystemWindowHandle;
        if (windowHandle == 0)
        {
            failure = "The window has no native handle to capture.";
            return null;
        }

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        Process process;
        try
        {
            process = Process.Start(CreateStartInfo(ffmpegPath, windowHandle, outputPath, resolvedOptions))
                ?? throw new InvalidOperationException("Process.Start returned no process.");
        }
        catch (Exception ex)
        {
            failure = $"ffmpeg '{ffmpegPath}' cannot start: {ex.Message}";
            return null;
        }

        string baseTitle = window.WindowTitle;
        EditorWindowVideoSession session = new(window, process, outputPath, baseTitle, resolvedOptions);
        session.BeginReadingStderr();

        // A bad capture source (a minimized window, a missing filter option, or an output
        // path that cannot be written) makes ffmpeg exit at once. Wait briefly so that this
        // case fails here instead of as a recording that shows a timer but writes nothing.
        if (process.WaitForExit(StartupProbe))
        {
            failure = session.DescribeExit();
            process.Dispose();
            return null;
        }

        failure = null;
        return session;
    }

    /// <summary>
    /// Starts the window title timer and watches ffmpeg for an exit that nobody asked for.
    /// Call it once, after the <see cref="Failed"/> subscription.
    /// </summary>
    public void Begin()
    {
        _titleTimer = new Timer(static state => ((EditorWindowVideoSession)state!).UpdateTitle(), this, TimeSpan.Zero, TitleInterval);
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;
        // The Exited event does not fire for a process that exited before the subscription,
        // so check once more. OnProcessExited ignores a second call.
        if (_process.HasExited)
            OnProcessExited(_process, EventArgs.Empty);
    }

    /// <summary>
    /// Asks ffmpeg to stop, waits for it to write the MP4 index, and puts the window title back.
    /// Kills ffmpeg when it does not exit in time; the MP4 is then not playable.
    /// </summary>
    /// <returns>True when ffmpeg exited with code 0 and the MP4 file exists.</returns>
    public bool Stop(out string? failure)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            failure = "The recording is already stopped.";
            return false;
        }

        _elapsed.Stop();
        try
        {
            if (!_process.HasExited)
            {
                // ffmpeg reads keyboard commands from stdin. 'q' ends the input, flushes the
                // encoder, and writes the MP4 index (moved to the front by +faststart).
                _process.StandardInput.Write('q');
                _process.StandardInput.Flush();
            }
        }
        catch (Exception)
        {
            // The pipe closes when ffmpeg exits by itself. The exit code check below
            // reports that case.
        }

        bool exited = _process.WaitForExit(StopWait);
        if (!exited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // The process exited between the wait and the kill.
            }
            _process.WaitForExit();
        }

        EndTitleUpdates();

        if (!exited)
            failure = $"ffmpeg did not finish the MP4 in {StopWait.TotalSeconds:0} s and was stopped. '{OutputPath}' is probably not playable.";
        else if (_process.ExitCode != 0)
            failure = DescribeExit();
        else if (!File.Exists(OutputPath))
            failure = $"ffmpeg exited with code 0 but wrote no file at '{OutputPath}'.";
        else
            failure = null;

        _process.Dispose();
        return failure is null;
    }

    /// <summary>
    /// Formats an elapsed time for the window title: <c>m:ss</c> below one hour and
    /// <c>h:mm:ss</c> from one hour.
    /// </summary>
    internal static string FormatElapsed(TimeSpan elapsed)
    {
        long totalSeconds = Math.Max(0L, (long)elapsed.TotalSeconds);
        long hours = totalSeconds / 3600;
        long minutes = totalSeconds / 60 % 60;
        long seconds = totalSeconds % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{totalSeconds / 60}:{seconds:00}");
    }

    /// <summary>Returns the window title that shows an active recording.</summary>
    internal static string FormatRecordingTitle(string baseTitle, TimeSpan elapsed)
        => string.IsNullOrEmpty(baseTitle)
            ? $"● REC {FormatElapsed(elapsed)}"
            : $"{baseTitle}  ● REC {FormatElapsed(elapsed)}";

    /// <summary>
    /// Builds the ffmpeg command line. The capture graph reads the window at up to 60 frames
    /// per second, copies the GPU frames to system memory, and scales the image down to fit
    /// 1920x1080 with even dimensions (yuv420p needs even dimensions). The encoder settings
    /// suit social video upload: H.264 High profile, CRF 26 with a 8 Mbit/s peak cap, a
    /// keyframe every two seconds, no audio, and the MP4 index at the front of the file.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(
        string ffmpegPath,
        nint windowHandle,
        string outputPath,
        EditorWindowRecordingOptions? options = null)
    {
        EditorWindowRecordingOptions resolved = (options ?? new EditorWindowRecordingOptions()).Normalize();
        string fps = resolved.FramesPerSecond.ToString(CultureInfo.InvariantCulture);
        int cursor = resolved.IncludeCursor ? 1 : 0;
        string captureSource = string.Create(
            CultureInfo.InvariantCulture,
            $"gfxcapture=hwnd={(long)windowHandle}:max_framerate={resolved.FramesPerSecond}:capture_cursor={cursor}");
        string scaleFilter = string.Create(
            CultureInfo.InvariantCulture,
            $"scale='min({resolved.MaxWidth},iw)':'min({resolved.MaxHeight},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2");
        string captureGraph = $"{captureSource},hwdownload,format=bgra,{scaleFilter}";

        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
        };

        bool useNvenc = ResolveUseNvenc(ffmpegPath, resolved.Encoder);
        List<string> arguments =
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-nostats",
            "-y",
            "-f", "lavfi",
            "-i", captureGraph,
            "-r", fps,
        ];
        AppendVideoEncoderArguments(arguments, resolved, useNvenc);
        arguments.AddRange([
            "-pix_fmt", "yuv420p",
            "-g", (resolved.FramesPerSecond * 2).ToString(CultureInfo.InvariantCulture),
            "-an",
            "-movflags", "+faststart",
            outputPath,
        ]);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    internal static bool ResolveUseNvenc(string ffmpegPath, EEditorWindowVideoEncoder requested)
        => requested switch
        {
            EEditorWindowVideoEncoder.Nvenc => true,
            EEditorWindowVideoEncoder.X264 => false,
            _ => EditorVideoFfmpegLocator.SupportsNvenc(ffmpegPath),
        };

    internal static void AppendVideoEncoderArguments(
        List<string> arguments,
        EditorWindowRecordingOptions options,
        bool useNvenc)
    {
        string bufsize = ResolveBufferSize(options.MaxBitrate);
        if (useNvenc)
        {
            arguments.AddRange([
                "-c:v", "h264_nvenc",
                "-preset", "p4",
                "-tune", "hq",
                "-rc", "vbr",
                "-cq", options.Crf.ToString(CultureInfo.InvariantCulture),
                "-b:v", "0",
                "-maxrate", options.MaxBitrate,
                "-bufsize", bufsize,
                "-profile:v", "high",
                "-level:v", "4.2",
            ]);
            return;
        }

        arguments.AddRange([
            "-c:v", "libx264",
            "-preset", options.Preset,
            "-crf", options.Crf.ToString(CultureInfo.InvariantCulture),
            "-maxrate", options.MaxBitrate,
            "-bufsize", bufsize,
            "-profile:v", "high",
            "-level:v", "4.2",
        ]);
    }

    internal static string ResolveBufferSize(string maxBitrate)
    {
        if (string.IsNullOrWhiteSpace(maxBitrate))
            return "16M";

        string trimmed = maxBitrate.Trim();
        char suffix = trimmed[^1];
        if ((suffix is 'M' or 'm' or 'K' or 'k') &&
            double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            value > 0.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{value * 2.0:0.##}{char.ToUpperInvariant(suffix)}");
        }

        return "16M";
    }

    private void BeginReadingStderr()
    {
        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            lock (_stderrTail)
            {
                if (_stderrTail.Count == StderrLineCount)
                    _stderrTail.Dequeue();
                _stderrTail.Enqueue(e.Data);
            }
        };
        _process.BeginErrorReadLine();
    }

    private string DescribeExit()
    {
        string tail;
        lock (_stderrTail)
            tail = string.Join(" | ", _stderrTail);

        string message = $"ffmpeg exited with code {_process.ExitCode}.";
        return string.IsNullOrEmpty(tail) ? message : $"{message} {tail}";
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _stopRequested) != 0)
            return;
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
            return;

        _elapsed.Stop();
        // Exited can fire before the last stderr lines arrive. This wait drains the
        // asynchronous stderr reader so that the failure message holds them.
        _process.WaitForExit();
        EndTitleUpdates();
        string message = DescribeExit();
        _process.Dispose();
        Failed?.Invoke(this, message);
    }

    private void UpdateTitle()
    {
        if (Volatile.Read(ref _stopRequested) != 0)
            return;

        // The timer fires four times per second, but the title shows whole seconds. Send a
        // title change to the window thread only when the shown value changes.
        TimeSpan elapsed = _elapsed.Elapsed;
        if (Options.MaxDuration is { } maxDuration && elapsed >= maxDuration)
        {
            if (Interlocked.Exchange(ref _autoStopTriggered, 1) == 0)
                _ = Task.Run(() => Completed?.Invoke(this));
            return;
        }

        long second = (long)elapsed.TotalSeconds;
        if (Interlocked.Exchange(ref _lastTitleSecond, second) == second)
            return;

        _window.RequestTitle(FormatRecordingTitle(BaseTitle, elapsed));
    }

    private void EndTitleUpdates()
    {
        Timer? timer = Interlocked.Exchange(ref _titleTimer, null);
        if (timer is null)
            return;

        using ManualResetEvent timerDone = new(false);
        // Dispose(WaitHandle) signals after any running callback ends, so no late callback
        // writes the recording title after the base title is back.
        if (timer.Dispose(timerDone))
            timerDone.WaitOne();
        _window.RequestTitle(BaseTitle);
    }
}

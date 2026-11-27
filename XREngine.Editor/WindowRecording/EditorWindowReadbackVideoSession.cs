using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using XREngine.Data.Geometry;
using XREngine.Imaging;
using XREngine.Rendering;

namespace XREngine.Editor.WindowRecording;

/// <summary>
/// Records an editor window to an MP4 file by queuing asynchronous composited GPU readbacks on
/// rendered frames and piping raw pixels to one ffmpeg process (hardware NVIDIA NVENC when
/// available, or libx264 fallback). Pixel conversion and vertical flips run exclusively on the
/// dedicated background writer thread so the render thread performs zero pixel copies.
/// Frames are paced to real wall-clock time at the configured output frame rate, duplicating
/// the latest captured frame across loading hitches so playback matches real time.
/// </summary>
internal sealed class EditorWindowReadbackVideoSession : IEditorWindowVideoSession
{
    /// <summary>Maximum simultaneous GPU readbacks in flight before skipping new frames.</summary>
    private const int MaxInFlightReadbacks = 3;

    /// <summary>Maximum raw readback images queued for the background writer thread.</summary>
    private const int MaxQueuedWriterImages = 4;

    /// <summary>Caps frame duplication across one stall (for example a debugger break) at 10 seconds.</summary>
    private const int MaxPadSecondsPerGap = 10;

    /// <summary>Number of ffmpeg stderr lines retained for failure diagnostics.</summary>
    private const int StderrLineCount = 12;

    /// <summary>Maximum time to wait for in-flight GPU readbacks when stopping.</summary>
    private static readonly TimeSpan InFlightDrainWait = TimeSpan.FromMilliseconds(500);

    /// <summary>Maximum time to wait for ffmpeg to flush and finalize the MP4 index.</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(20);

    /// <summary>Interval for updating the window title elapsed timer and checking <see cref="EditorWindowRecordingOptions.MaxDuration"/>.</summary>
    private static readonly TimeSpan TitleInterval = TimeSpan.FromMilliseconds(250);

    private readonly XRWindow _window;
    private readonly string _ffmpegPath;
    private readonly Stopwatch _elapsed;
    private readonly double _minScheduleIntervalSeconds;
    private readonly Queue<string> _stderrTail = new(StderrLineCount);
    private readonly object _queueGate = new();
    private readonly Queue<QueuedImage> _pendingImages = new(MaxQueuedWriterImages);
    private readonly AutoResetEvent _frameAvailable = new(false);

    private Thread? _writerThread;
    private Process? _process;
    private Stream? _stdin;
    private Timer? _titleTimer;

    private int _videoWidth;
    private int _videoHeight;
    private int _bytesPerPixel;
    private int _frameBytes;
    private string? _rawPixelFormat;
    private byte[]? _heldFrame;
    private byte[]? _scratchFrame;
    private long _firstFrameTimestamp;
    private long _lastScheduledTimestamp;
    private long _lastTitleSecond = -1;

    private int _inFlightReadbacks;
    private long _capturedFrameCount;
    private long _writtenVideoFrameCount;
    private long _droppedReadbackCount;
    private int _stopRequested;
    private int _autoStopTriggered;
    private int _writerDone;
    private string? _writerFailure;

    private readonly record struct QueuedImage(RuntimeImage Image, long CaptureTimestamp);

    private EditorWindowReadbackVideoSession(
        XRWindow window,
        string ffmpegPath,
        string outputPath,
        string baseTitle,
        EditorWindowRecordingOptions options)
    {
        _window = window;
        _ffmpegPath = ffmpegPath;
        OutputPath = outputPath;
        BaseTitle = baseTitle;
        Options = options;
        _elapsed = Stopwatch.StartNew();
        // Leave small pacing headroom so a 60 Hz render loop does not beat against a 60 fps cap.
        _minScheduleIntervalSeconds = 0.85 / options.FramesPerSecond;
    }

    /// <inheritdoc/>
    public string OutputPath { get; }

    /// <inheritdoc/>
    public string BaseTitle { get; }

    /// <inheritdoc/>
    public XRWindow Window => _window;

    /// <inheritdoc/>
    public EEditorWindowRecordingBackend Backend => EEditorWindowRecordingBackend.RendererReadback;

    /// <inheritdoc/>
    public EditorWindowRecordingOptions Options { get; }

    /// <inheritdoc/>
    public TimeSpan Elapsed => _elapsed.Elapsed;

    /// <inheritdoc/>
    public long CapturedFrameCount => Volatile.Read(ref _capturedFrameCount);

    /// <inheritdoc/>
    public long WrittenVideoFrameCount => Volatile.Read(ref _writtenVideoFrameCount);

    /// <inheritdoc/>
    public long DroppedReadbackCount => Volatile.Read(ref _droppedReadbackCount);

    /// <inheritdoc/>
    public event Action<IEditorWindowVideoSession, string>? Failed;

    /// <inheritdoc/>
    public event Action<IEditorWindowVideoSession>? Completed;

    /// <summary>
    /// Creates a new renderer readback session for <paramref name="window"/>. Starts ffmpeg
    /// lazily on the writer thread when the first GPU readback arrives so the exact pixel format
    /// and window dimensions are known.
    /// </summary>
    public static EditorWindowReadbackVideoSession? TryStart(
        XRWindow window,
        string ffmpegPath,
        string outputPath,
        EditorWindowRecordingOptions options,
        out string? failure)
    {
        if (window.IsDisposed)
        {
            failure = "The target window is disposed.";
            return null;
        }

        EditorWindowRecordingOptions normalized = options.Normalize();
        if (normalized.Encoder == EEditorWindowVideoEncoder.Nvenc &&
            !EditorVideoFfmpegLocator.SupportsNvenc(ffmpegPath))
        {
            failure = $"Requested NVIDIA NVENC (h264_nvenc), but '{ffmpegPath}' cannot encode with h264_nvenc on this GPU.";
            return null;
        }

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        failure = null;
        return new EditorWindowReadbackVideoSession(window, ffmpegPath, outputPath, window.WindowTitle, normalized);
    }

    /// <inheritdoc/>
    public void Begin()
    {
        _writerThread = new Thread(RunWriterLoop)
        {
            Name = "EditorWindowReadbackVideoWriter",
            IsBackground = true,
        };
        _writerThread.Start();

        _window.PostRenderViewportsCallback += OnPostRenderViewports;
        _titleTimer = new Timer(static state => ((EditorWindowReadbackVideoSession)state!).UpdateTitle(), this, TimeSpan.Zero, TitleInterval);
    }

    /// <inheritdoc/>
    public bool Stop(out string? failure)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            failure = "The recording is already stopped.";
            return false;
        }

        _window.PostRenderViewportsCallback -= OnPostRenderViewports;
        _elapsed.Stop();
        EndTitleUpdates();

        // Give in-flight GPU readbacks a short window to land so the final frames are kept.
        long drainStart = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref _inFlightReadbacks) > 0 &&
               Stopwatch.GetElapsedTime(drainStart) < InFlightDrainWait)
        {
            Thread.Sleep(5);
        }

        _frameAvailable.Set();
        if (_writerThread is { IsAlive: true })
            _writerThread.Join(StopWait + TimeSpan.FromSeconds(5));

        _frameAvailable.Dispose();

        string? writerFailure = Volatile.Read(ref _writerFailure);
        if (writerFailure is not null)
        {
            failure = writerFailure;
            return false;
        }

        if (CapturedFrameCount == 0)
        {
            failure = "The renderer readback recording captured no frames before stopping.";
            return false;
        }

        if (!File.Exists(OutputPath))
        {
            failure = $"ffmpeg exited cleanly but wrote no file at '{OutputPath}'.";
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// Builds the ffmpeg argument list that reads raw pixels from stdin, preserves exact window
    /// dimensions when already within <see cref="EditorWindowRecordingOptions.MaxWidth"/> by
    /// <see cref="EditorWindowRecordingOptions.MaxHeight"/> (and even), and encodes with NVENC
    /// or libx264 into a fast-start H.264 MP4.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(
        string ffmpegPath,
        int width,
        int height,
        string rawPixelFormat,
        string outputPath,
        EditorWindowRecordingOptions options)
    {
        string fps = options.FramesPerSecond.ToString(CultureInfo.InvariantCulture);
        bool useNvenc = EditorWindowVideoSession.ResolveUseNvenc(ffmpegPath, options.Encoder);
        bool needsScale = width > options.MaxWidth || height > options.MaxHeight || (width & 1) != 0 || (height & 1) != 0;

        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
        };

        List<string> arguments =
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-nostats",
            "-y",
            "-f", "rawvideo",
            "-pix_fmt", rawPixelFormat,
            "-video_size", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}"),
            "-framerate", fps,
            "-i", "pipe:0",
        ];

        if (needsScale)
        {
            string scaleFilter = string.Create(
                CultureInfo.InvariantCulture,
                $"scale='min({options.MaxWidth},iw)':'min({options.MaxHeight},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2");
            arguments.Add("-vf");
            arguments.Add(scaleFilter);
        }

        EditorWindowVideoSession.AppendVideoEncoderArguments(arguments, options, useNvenc);
        arguments.AddRange([
            "-pix_fmt", "yuv420p",
            "-g", (options.FramesPerSecond * 2).ToString(CultureInfo.InvariantCulture),
            "-an",
            "-movflags", "+faststart",
            outputPath,
        ]);

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    private void OnPostRenderViewports()
    {
        if (Volatile.Read(ref _stopRequested) != 0 || Volatile.Read(ref _writerDone) != 0)
            return;

        long now = Stopwatch.GetTimestamp();
        long previous = Volatile.Read(ref _lastScheduledTimestamp);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now).TotalSeconds < _minScheduleIntervalSeconds)
            return;

        if (Volatile.Read(ref _inFlightReadbacks) >= MaxInFlightReadbacks)
        {
            Interlocked.Increment(ref _droppedReadbackCount);
            return;
        }

        AbstractRenderer? renderer = AbstractRenderer.Current ?? (_window.IsDisposed ? null : _window.Renderer);
        if (renderer is null)
            return;

        var size = _window.RenderFramebufferSize;
        if (size.X <= 0 || size.Y <= 0)
            return;

        BoundingRectangle region = new(0, 0, size.X, size.Y);
        Interlocked.Increment(ref _inFlightReadbacks);
        Volatile.Write(ref _lastScheduledTimestamp, now);

        bool queued = renderer.TryQueueCompositedScreenshotReadback(region, result => OnReadbackCompleted(result, now), out _);
        if (!queued)
            queued = renderer.TryQueueScreenshotReadback(region, withTransparency: false, result => OnReadbackCompleted(result, now), out _);

        if (!queued)
        {
            Interlocked.Decrement(ref _inFlightReadbacks);
            Interlocked.Increment(ref _droppedReadbackCount);
        }
    }

    private void OnReadbackCompleted(ScreenshotReadbackResult result, long captureTimestamp)
    {
        RuntimeImage? ownershipNotTransferred = result.Image;
        try
        {
            if (Volatile.Read(ref _stopRequested) != 0 || Volatile.Read(ref _writerDone) != 0)
                return;

            if (!result.Succeeded || result.Image is null)
            {
                Interlocked.Increment(ref _droppedReadbackCount);
                return;
            }

            // Hand off the RuntimeImage directly to the background writer thread so the render
            // thread performs zero pixel copies or row flips.
            bool enqueued = false;
            lock (_queueGate)
            {
                if (Volatile.Read(ref _writerDone) == 0)
                {
                    if (_pendingImages.Count >= MaxQueuedWriterImages)
                    {
                        QueuedImage oldest = _pendingImages.Dequeue();
                        oldest.Image.Dispose();
                        Interlocked.Increment(ref _droppedReadbackCount);
                    }

                    _pendingImages.Enqueue(new QueuedImage(result.Image, captureTimestamp));
                    ownershipNotTransferred = null;
                    Interlocked.Increment(ref _capturedFrameCount);
                    enqueued = true;
                }
            }

            if (enqueued)
                _frameAvailable.Set();
        }
        finally
        {
            ownershipNotTransferred?.Dispose();
            Interlocked.Decrement(ref _inFlightReadbacks);
        }
    }

    private void RunWriterLoop()
    {
        try
        {
            while (true)
            {
                QueuedImage next;
                bool hasImage;
                lock (_queueGate)
                {
                    hasImage = _pendingImages.TryDequeue(out next);
                }

                if (!hasImage)
                {
                    if (Volatile.Read(ref _stopRequested) != 0)
                        break;
                    _frameAvailable.WaitOne(50);
                    continue;
                }

                try
                {
                    ProcessQueuedImage(next);
                }
                finally
                {
                    next.Image.Dispose();
                }
            }

            // Pad the final held frame up to the real-time stop boundary so the ending is not clipped.
            if (_heldFrame is not null && _stdin is not null && _firstFrameTimestamp != 0)
            {
                double totalElapsedSeconds = Stopwatch.GetElapsedTime(_firstFrameTimestamp).TotalSeconds;
                long finalTargetIndex = (long)Math.Floor(totalElapsedSeconds * Options.FramesPerSecond);
                WritePaddedCopies(_heldFrame, finalTargetIndex);
            }

            FinalizeFfmpegProcess();
        }
        catch (Exception ex)
        {
            string message = BuildFailureMessage(ex.Message);
            Volatile.Write(ref _writerFailure, message);
            CleanupProcessOnFailure();
            if (Volatile.Read(ref _stopRequested) == 0)
            {
                Interlocked.Exchange(ref _stopRequested, 1);
                _window.PostRenderViewportsCallback -= OnPostRenderViewports;
                _elapsed.Stop();
                EndTitleUpdates();
                Failed?.Invoke(this, message);
            }
        }
        finally
        {
            Volatile.Write(ref _writerDone, 1);
            ReturnAllBuffers();
        }
    }

    private void ProcessQueuedImage(QueuedImage item)
    {
        RuntimeImage image = item.Image;
        if (!TryResolveRawPixelFormat(image.PixelFormat, out string rawPixelFormat, out int bytesPerPixel))
        {
            Interlocked.Increment(ref _droppedReadbackCount);
            return;
        }

        int width = (int)image.Width;
        int height = (int)image.Height;
        if (width <= 0 || height <= 0)
            return;

        if (_process is null)
            StartFfmpegProcess(width, height, rawPixelFormat, bytesPerPixel);

        if (bytesPerPixel != _bytesPerPixel)
        {
            Interlocked.Increment(ref _droppedReadbackCount);
            return;
        }

        // Convert into the reusable scratch buffer (no per-frame ArrayPool churn on steady state).
        byte[] targetBuffer = _scratchFrame!;
        if (width == _videoWidth && height == _videoHeight)
            CopyTopFirstRows(image, targetBuffer, bytesPerPixel);
        else
            CopyResizedTopFirstRows(image, targetBuffer, _videoWidth, _videoHeight, bytesPerPixel);

        if (_firstFrameTimestamp == 0)
        {
            _firstFrameTimestamp = item.CaptureTimestamp;
            WriteRawFrame(targetBuffer);
            SwapHeldAndScratchBuffers();
            return;
        }

        double elapsedSeconds = Math.Max(0.0, Stopwatch.GetElapsedTime(_firstFrameTimestamp, item.CaptureTimestamp).TotalSeconds);
        long targetIndex = (long)Math.Floor(elapsedSeconds * Options.FramesPerSecond);

        if (targetIndex < _writtenVideoFrameCount)
        {
            // Multiple captures landed inside one output frame slot; keep the freshest pixels.
            SwapHeldAndScratchBuffers();
            return;
        }

        if (_heldFrame is not null && targetIndex > _writtenVideoFrameCount)
            WritePaddedCopies(_heldFrame, targetIndex);

        WriteRawFrame(targetBuffer);
        SwapHeldAndScratchBuffers();
    }

    private void SwapHeldAndScratchBuffers()
        => (_heldFrame, _scratchFrame) = (_scratchFrame, _heldFrame);

    private void WritePaddedCopies(byte[] sourceBuffer, long targetExclusiveIndex)
    {
        long missing = targetExclusiveIndex - _writtenVideoFrameCount;
        if (missing <= 0)
            return;

        long maxGapFrames = (long)Options.FramesPerSecond * MaxPadSecondsPerGap;
        if (missing > maxGapFrames)
            missing = maxGapFrames;

        for (long i = 0; i < missing; i++)
            WriteRawFrame(sourceBuffer);
    }

    private void WriteRawFrame(byte[] buffer)
    {
        _stdin!.Write(buffer, 0, _frameBytes);
        Interlocked.Increment(ref _writtenVideoFrameCount);
    }

    private void StartFfmpegProcess(int width, int height, string rawPixelFormat, int bytesPerPixel)
    {
        _videoWidth = width;
        _videoHeight = height;
        _bytesPerPixel = bytesPerPixel;
        _rawPixelFormat = rawPixelFormat;
        _frameBytes = checked(_videoWidth * _videoHeight * _bytesPerPixel);
        _heldFrame = ArrayPool<byte>.Shared.Rent(_frameBytes);
        _scratchFrame = ArrayPool<byte>.Shared.Rent(_frameBytes);

        ProcessStartInfo startInfo = CreateStartInfo(
            _ffmpegPath,
            _videoWidth,
            _videoHeight,
            _rawPixelFormat,
            OutputPath,
            Options);

        bool useNvenc = EditorWindowVideoSession.ResolveUseNvenc(_ffmpegPath, Options.Encoder);
        Debug.Out(
            $"[WindowRecording] Starting readback ffmpeg pipe ({_videoWidth}x{_videoHeight} {_rawPixelFormat} -> " +
            $"{(useNvenc ? "h264_nvenc" : "libx264")} @ {Options.FramesPerSecond} fps) to '{OutputPath}'.");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{_ffmpegPath}'.");
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        process.ErrorDataReceived += (_, e) =>
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
        process.BeginErrorReadLine();
    }

    private void FinalizeFfmpegProcess()
    {
        if (_stdin is not null)
        {
            try
            {
                _stdin.Flush();
                _stdin.Dispose();
            }
            catch (Exception)
            {
                // Pipe may already be closed if ffmpeg exited early.
            }
            _stdin = null;
        }

        if (_process is null)
            return;

        bool exited = _process.WaitForExit(StopWait);
        if (!exited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
            _process.WaitForExit();
            _process.Dispose();
            _process = null;
            throw new InvalidOperationException($"ffmpeg did not finish writing '{OutputPath}' within {StopWait.TotalSeconds:0} s.");
        }

        int exitCode = _process.ExitCode;
        _process.Dispose();
        _process = null;

        if (exitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {exitCode}.");
    }

    private void CleanupProcessOnFailure()
    {
        if (_stdin is not null)
        {
            try { _stdin.Dispose(); } catch (Exception) { }
            _stdin = null;
        }

        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private void ReturnAllBuffers()
    {
        if (_heldFrame is not null)
        {
            ArrayPool<byte>.Shared.Return(_heldFrame);
            _heldFrame = null;
        }

        if (_scratchFrame is not null)
        {
            ArrayPool<byte>.Shared.Return(_scratchFrame);
            _scratchFrame = null;
        }

        lock (_queueGate)
        {
            while (_pendingImages.TryDequeue(out QueuedImage queued))
                queued.Image.Dispose();
        }
    }

    private string BuildFailureMessage(string message)
    {
        string tail;
        lock (_stderrTail)
            tail = string.Join(" | ", _stderrTail);
        return string.IsNullOrEmpty(tail) ? message : $"{message} {tail}";
    }

    private void UpdateTitle()
    {
        if (Volatile.Read(ref _stopRequested) != 0)
            return;

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

        _window.RequestTitle(EditorWindowVideoSession.FormatRecordingTitle(BaseTitle, elapsed));
    }

    private void EndTitleUpdates()
    {
        Timer? timer = Interlocked.Exchange(ref _titleTimer, null);
        if (timer is null)
            return;

        using ManualResetEvent timerDone = new(false);
        if (timer.Dispose(timerDone))
            timerDone.WaitOne();
        _window.RequestTitle(BaseTitle);
    }

    private static bool TryResolveRawPixelFormat(RuntimePixelFormat pixelFormat, out string rawFormat, out int bytesPerPixel)
    {
        switch (pixelFormat)
        {
            case RuntimePixelFormat.Rgba8:
                rawFormat = "rgba";
                bytesPerPixel = 4;
                return true;
            case RuntimePixelFormat.Bgra8:
                rawFormat = "bgra";
                bytesPerPixel = 4;
                return true;
            case RuntimePixelFormat.Rgb8:
                rawFormat = "rgb24";
                bytesPerPixel = 3;
                return true;
            case RuntimePixelFormat.Bgr8:
                rawFormat = "bgr24";
                bytesPerPixel = 3;
                return true;
            default:
                rawFormat = string.Empty;
                bytesPerPixel = 0;
                return false;
        }
    }

    private static void CopyTopFirstRows(RuntimeImage image, byte[] destination, int bytesPerPixel)
    {
        int width = (int)image.Width;
        int height = (int)image.Height;
        int rowBytes = width * bytesPerPixel;
        ReadOnlySpan<byte> source = image.Pixels.Span;
        Span<byte> target = destination.AsSpan(0, rowBytes * height);
        bool bottomFirst = image.Origin == RuntimeImageOrigin.BottomLeft;
        if (!bottomFirst && image.RowStrideBytes == rowBytes)
        {
            source.Slice(0, rowBytes * height).CopyTo(target);
            return;
        }

        for (int row = 0; row < height; row++)
        {
            int sourceRow = bottomFirst ? height - 1 - row : row;
            source.Slice(sourceRow * image.RowStrideBytes, rowBytes).CopyTo(target.Slice(row * rowBytes, rowBytes));
        }
    }

    /// <summary>
    /// Nearest-neighbor resizes an incoming readback image into the locked ffmpeg input frame
    /// dimensions when the editor window resizes mid-recording.
    /// </summary>
    private static void CopyResizedTopFirstRows(RuntimeImage image, byte[] destination, int targetWidth, int targetHeight, int bytesPerPixel)
    {
        int sourceWidth = (int)image.Width;
        int sourceHeight = (int)image.Height;
        int stride = image.RowStrideBytes;
        int targetRowBytes = targetWidth * bytesPerPixel;
        ReadOnlySpan<byte> source = image.Pixels.Span;
        Span<byte> target = destination.AsSpan(0, targetRowBytes * targetHeight);
        bool bottomFirst = image.Origin == RuntimeImageOrigin.BottomLeft;

        for (int y = 0; y < targetHeight; y++)
        {
            int sy = (int)((long)y * sourceHeight / targetHeight);
            int sourceRow = bottomFirst ? sourceHeight - 1 - sy : sy;
            ReadOnlySpan<byte> srcRowSpan = source.Slice(sourceRow * stride, sourceWidth * bytesPerPixel);
            Span<byte> dstRowSpan = target.Slice(y * targetRowBytes, targetRowBytes);
            for (int x = 0; x < targetWidth; x++)
            {
                int sx = (int)((long)x * sourceWidth / targetWidth);
                srcRowSpan.Slice(sx * bytesPerPixel, bytesPerPixel).CopyTo(dstRowSpan.Slice(x * bytesPerPixel, bytesPerPixel));
            }
        }
    }
}

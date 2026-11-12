using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using XREngine.Editor.Mcp;
using XREngine.Imaging;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Sends captured cutscene frames as raw pixels to the standard input of one ffmpeg process, which encodes the master
/// MP4 directly. This replaces one PNG encode and one PNG decode per frame.
/// </summary>
/// <remarks>
/// Capture sessions finish readbacks on worker threads in any order. The pipe keeps early frames in pooled buffers
/// and writes them in global frame order. The bounded readback queue of the capture session limits the number of
/// waiting frames. ffmpeg starts when the first frame arrives, because the readback pixel layout sets the
/// <c>rawvideo</c> pixel format.
/// </remarks>
internal sealed class FfmpegRawVideoPipe : IViewportSequenceFrameSink, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, byte[]> _pendingFrames = [];
    private readonly string _ffmpegPath;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _totalFrames;
    private readonly ECutsceneVideoEncoder _encoder;
    private readonly string _outputMp4Path;
    private readonly int _posterFrameIndex;
    private readonly string _posterPngPath;

    private Process? _process;
    private Stream? _input;
    private Task<string>? _stderrTask;
    private Task<string>? _stdoutTask;
    private string? _rawPixelFormat;
    private int _frameBytes;
    private int _nextFrameIndex;
    private int _segmentOffset;
    private Exception? _failure;
    private bool _completed;

    /// <summary>
    /// Creates a pipe that encodes <paramref name="totalFrames"/> frames into <paramref name="outputMp4Path"/>.
    /// </summary>
    /// <param name="ffmpegPath">The ffmpeg executable.</param>
    /// <param name="width">The width of every frame, in pixels.</param>
    /// <param name="height">The height of every frame, in pixels.</param>
    /// <param name="fps">The frame rate of the output video.</param>
    /// <param name="totalFrames">The number of frames that the video must contain.</param>
    /// <param name="encoder">The H.264 encoder for the master video.</param>
    /// <param name="outputMp4Path">The master MP4 file to write.</param>
    /// <param name="posterFrameIndex">The global frame index that is also saved as a lossless PNG poster.</param>
    /// <param name="posterPngPath">The PNG file for the poster frame.</param>
    public FfmpegRawVideoPipe(
        string ffmpegPath,
        int width,
        int height,
        int fps,
        int totalFrames,
        ECutsceneVideoEncoder encoder,
        string outputMp4Path,
        int posterFrameIndex,
        string posterPngPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalFrames);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputMp4Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(posterPngPath);

        _ffmpegPath = ffmpegPath;
        _width = width;
        _height = height;
        _fps = fps;
        _totalFrames = totalFrames;
        _encoder = encoder;
        _outputMp4Path = outputMp4Path;
        _posterFrameIndex = posterFrameIndex;
        _posterPngPath = posterPngPath;
    }

    /// <summary>
    /// Gets or sets the global frame index of capture index zero in the active capture session. The cutscene recorder
    /// sets this value before each capture segment so that session-local indexes map to one video timeline.
    /// </summary>
    public int SegmentOffset
    {
        get
        {
            lock (_gate)
                return _segmentOffset;
        }
        set
        {
            lock (_gate)
                _segmentOffset = value;
        }
    }

    /// <summary>Gets the number of frames that ffmpeg has received in order.</summary>
    public int FramesWritten
    {
        get
        {
            lock (_gate)
                return _nextFrameIndex;
        }
    }

    /// <inheritdoc/>
    public void WriteFrame(int captureIndex, RuntimeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width != (uint)_width || image.Height != (uint)_height)
            throw new InvalidOperationException(
                $"The ffmpeg pipe needs {_width}x{_height} frames, but frame {captureIndex} is {image.Width}x{image.Height}.");

        (string rawPixelFormat, int bytesPerPixel) = ResolveRawPixelFormat(image.PixelFormat);
        int globalFrameIndex;
        lock (_gate)
        {
            ThrowIfFailedOrCompleted();
            globalFrameIndex = _segmentOffset + captureIndex;
            if (_rawPixelFormat is not null && !string.Equals(_rawPixelFormat, rawPixelFormat, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Frame {globalFrameIndex} uses pixel format '{rawPixelFormat}', but the pipe started with '{_rawPixelFormat}'.");
        }

        if (globalFrameIndex < 0 || globalFrameIndex >= _totalFrames)
            throw new InvalidOperationException($"Frame index {globalFrameIndex} is outside the video range 0..{_totalFrames - 1}.");

        // The poster keeps the exact read-back pixels instead of a frame decoded from the lossy video.
        if (globalFrameIndex == _posterFrameIndex)
            File.WriteAllBytes(_posterPngPath, RuntimeImageCodecs.Require().EncodePng(image));

        int frameBytes = checked(_width * _height * bytesPerPixel);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
        try
        {
            CopyTopFirstRows(image, buffer, bytesPerPixel);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        lock (_gate)
        {
            try
            {
                ThrowIfFailedOrCompleted();
                EnsureStarted(rawPixelFormat, bytesPerPixel);
                if (globalFrameIndex < _nextFrameIndex || !_pendingFrames.TryAdd(globalFrameIndex, buffer))
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw new InvalidOperationException($"Frame {globalFrameIndex} reached the ffmpeg pipe more than once.");
                }

                WritePendingFramesInOrder();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _failure ??= CreatePipeFailure(ex);
                throw _failure;
            }
        }
    }

    /// <summary>
    /// Closes the ffmpeg input after every frame has arrived, waits for ffmpeg to finish the MP4, and throws when a
    /// frame is missing or ffmpeg reports an error.
    /// </summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        Process process;
        Task<string> stderrTask;
        Task<string> stdoutTask;
        lock (_gate)
        {
            if (_failure is not null)
                throw _failure;
            if (_completed)
                return;
            if (_process is null || _input is null || _stderrTask is null || _stdoutTask is null)
                throw new InvalidOperationException("The ffmpeg pipe received no frames.");
            if (_nextFrameIndex != _totalFrames || _pendingFrames.Count != 0)
                throw new InvalidOperationException(
                    $"The ffmpeg pipe received {_nextFrameIndex} of {_totalFrames} frames in order; {_pendingFrames.Count} frame(s) are still waiting for earlier frames.");

            _completed = true;
            _input.Flush();
            _input.Dispose();
            _input = null;
            process = _process;
            stderrTask = _stderrTask;
            stdoutTask = _stdoutTask;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode} while it encoded '{_outputMp4Path}': {stderr}");
    }

    /// <summary>
    /// Stops ffmpeg when the recording did not complete and returns every pooled frame buffer.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Process? process;
        lock (_gate)
        {
            foreach (byte[] buffer in _pendingFrames.Values)
                ArrayPool<byte>.Shared.Return(buffer);
            _pendingFrames.Clear();
            _input?.Dispose();
            _input = null;
            process = _process;
            _process = null;
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void EnsureStarted(string rawPixelFormat, int bytesPerPixel)
    {
        if (_process is not null)
            return;

        _rawPixelFormat = rawPixelFormat;
        _frameBytes = checked(_width * _height * bytesPerPixel);

        string? outputDirectory = Path.GetDirectoryName(_outputMp4Path);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        if (File.Exists(_outputMp4Path))
            File.Delete(_outputMp4Path);

        ProcessStartInfo startInfo = new()
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in BuildArguments())
            startInfo.ArgumentList.Add(argument);

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{_ffmpegPath}'.");
        _process = process;
        _input = process.StandardInput.BaseStream;
        // Drain both output streams from the start so that a full pipe buffer never blocks ffmpeg.
        _stderrTask = process.StandardError.ReadToEndAsync();
        _stdoutTask = process.StandardOutput.ReadToEndAsync();
    }

    private List<string> BuildArguments()
    {
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo",
            "-pix_fmt", _rawPixelFormat!,
            "-video_size", $"{_width}x{_height}",
            "-framerate", _fps.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
        ];
        arguments.AddRange(CutsceneVideoEncoderArguments.ForMaster(_encoder));
        arguments.AddRange(["-pix_fmt", "yuv420p", "-movflags", "+faststart", _outputMp4Path]);
        return arguments;
    }

    private void WritePendingFramesInOrder()
    {
        while (_pendingFrames.Remove(_nextFrameIndex, out byte[]? buffer))
        {
            try
            {
                _input!.Write(buffer, 0, _frameBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            _nextFrameIndex++;
        }
    }

    private void ThrowIfFailedOrCompleted()
    {
        if (_failure is not null)
            throw _failure;
        if (_completed)
            throw new InvalidOperationException("The ffmpeg pipe is already complete.");
    }

    private InvalidOperationException CreatePipeFailure(Exception inner)
    {
        string stderr = string.Empty;
        if (_stderrTask is { } stderrTask && stderrTask.Wait(TimeSpan.FromSeconds(2)))
            stderr = stderrTask.Result;
        return new InvalidOperationException(
            $"ffmpeg stopped accepting frames at frame {_nextFrameIndex} for '{_outputMp4Path}'. {stderr}".TrimEnd(),
            inner);
    }

    private static (string RawPixelFormat, int BytesPerPixel) ResolveRawPixelFormat(RuntimePixelFormat pixelFormat)
        => pixelFormat switch
        {
            RuntimePixelFormat.Rgba8 => ("rgba", 4),
            RuntimePixelFormat.Bgra8 => ("bgra", 4),
            RuntimePixelFormat.Rgb8 => ("rgb24", 3),
            RuntimePixelFormat.Bgr8 => ("bgr24", 3),
            _ => throw new NotSupportedException($"The ffmpeg pipe does not support readback pixel format '{pixelFormat}'."),
        };

    /// <summary>
    /// Copies the image rows into <paramref name="destination"/> as tightly packed rows with the top row first.
    /// Bottom-left readbacks are flipped here so that ffmpeg receives upright frames.
    /// </summary>
    private static void CopyTopFirstRows(RuntimeImage image, byte[] destination, int bytesPerPixel)
    {
        int width = (int)image.Width;
        int height = (int)image.Height;
        int rowBytes = width * bytesPerPixel;
        ReadOnlySpan<byte> source = image.Pixels.Span;
        Span<byte> target = destination.AsSpan(0, rowBytes * height);
        bool bottomFirst = image.Origin == RuntimeImageOrigin.BottomLeft;
        for (int row = 0; row < height; row++)
        {
            int sourceRow = bottomFirst ? height - 1 - row : row;
            source.Slice(sourceRow * image.RowStrideBytes, rowBytes).CopyTo(target.Slice(row * rowBytes, rowBytes));
        }
    }
}

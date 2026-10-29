using System.Diagnostics;
using System.Globalization;

namespace XREngine.Rendering.Models.Gaussian.Baking.Video;

/// <summary>
/// One running ffmpeg process that encodes one output of a preview loop. <see cref="FfmpegEncoder.StartAsync"/> starts it.
/// Write the frames with <see cref="WriteFrameAsync"/>, then call <see cref="CompleteAsync"/>. Dispose the session in all
/// cases: a session that is not complete stops its process.
/// </summary>
public sealed class FfmpegSession : IAsyncDisposable
{
    /// <summary>The number of stderr lines that a failure message holds.</summary>
    public const int StderrLineCount = 20;

    /// <summary>The longest time to wait for ffmpeg to end after it closed its input.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly Stream _input;
    private readonly int _frameByteCount;
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly Queue<string> _stderrTail = new(StderrLineCount);
    private readonly Task _stderrPump;
    private readonly Task _stdoutDrain;
    private bool _inputClosed;
    private bool _completed;
    private bool _disposed;

    internal FfmpegSession(Process process, LoopRendition rendition, string outputPath, int frameByteCount, CancellationToken cancellationToken)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _frameByteCount = frameByteCount;
        _cancellationToken = cancellationToken;
        Rendition = rendition;
        OutputPath = outputPath;
        _stderrPump = PumpStderrAsync(process.StandardError);
        _stdoutDrain = DrainAsync(process.StandardOutput.BaseStream);
        _cancellationRegistration = cancellationToken.Register(static state => ((FfmpegSession)state!).Kill(), this);
    }

    /// <summary>Gets the output of this session.</summary>
    public LoopRendition Rendition { get; }

    /// <summary>Gets the output file.</summary>
    public string OutputPath { get; }

    /// <summary>
    /// Writes one frame to the standard input of ffmpeg. The frame has width × height RGBA bytes, top row first.
    /// The caller can use the memory again when the returned task is complete.
    /// </summary>
    /// <exception cref="GaussianBakeException">
    /// ffmpeg stopped before it read the frame. The message holds the last stderr lines. The failure code is
    /// <see cref="GaussianBakeFailureCodes.EncodeFailed"/>.
    /// </exception>
    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> rgba)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inputClosed)
            throw new InvalidOperationException($"The ffmpeg session of '{FileName}' takes no more frames.");
        if (rgba.Length != _frameByteCount)
            throw new ArgumentException($"A frame must have {_frameByteCount} bytes, not {rgba.Length}.", nameof(rgba));
        _cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _input.WriteAsync(rgba, _cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (_cancellationToken.IsCancellationRequested)
        {
            // The cancellation stopped the process, and the pipe closed under the write.
            throw new OperationCanceledException("The ffmpeg session was canceled.", ex, _cancellationToken);
        }
        catch (IOException)
        {
            // ffmpeg closed its input, so it stopped. Its exit code and stderr tell why.
            throw await StoppedEarlyAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the input of ffmpeg and waits until ffmpeg has written the output.
    /// </summary>
    /// <exception cref="GaussianBakeException">
    /// ffmpeg stopped with an exit code that is not 0. The message holds the last stderr lines. The failure code is
    /// <see cref="GaussianBakeFailureCodes.EncodeFailed"/>.
    /// </exception>
    public async Task CompleteAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            return;

        CloseInput();
        try
        {
            await _process.WaitForExitAsync(_cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(_stderrPump, _stdoutDrain).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw;
        }

        if (_process.ExitCode != 0)
            throw CreateFailure($"stopped with exit code {_process.ExitCode.ToString(CultureInfo.InvariantCulture)}");
        _completed = true;
    }

    /// <summary>Stops ffmpeg when the session is not complete, waits for it to end, and releases the process.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await _cancellationRegistration.DisposeAsync().ConfigureAwait(false);
        if (!_completed)
            await FfmpegEncoder.StopAsync(_process).ConfigureAwait(false);
        CloseInput();
        try
        {
            await Task.WhenAll(_stderrPump, _stdoutDrain).WaitAsync(ExitWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The pipes stay open only when the system did not stop the process. The process is released below.
        }

        _process.Dispose();
    }

    private string FileName => Path.GetFileName(OutputPath);

    private async Task<GaussianBakeException> StoppedEarlyAsync()
    {
        CloseInput();
        try
        {
            await _process.WaitForExitAsync(_cancellationToken).WaitAsync(ExitWait).ConfigureAwait(false);
            await Task.WhenAll(_stderrPump, _stdoutDrain).WaitAsync(ExitWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Kill();
            return CreateFailure($"closed its input but did not stop in {ExitWait.TotalSeconds:F0} seconds");
        }

        return CreateFailure($"stopped with exit code {_process.ExitCode.ToString(CultureInfo.InvariantCulture)} before it read all frames");
    }

    private GaussianBakeException CreateFailure(string what)
    {
        string[] lines;
        lock (_stderrTail)
            lines = [.. _stderrTail];
        return new GaussianBakeException(
            GaussianBakeFailureCodes.EncodeFailed,
            $"ffmpeg {what} while it encoded '{FileName}'.{FfmpegEncoder.FormatStderr(lines)}");
    }

    private void CloseInput()
    {
        if (_inputClosed)
            return;
        _inputClosed = true;
        try
        {
            _input.Dispose();
        }
        catch (IOException)
        {
            // ffmpeg stopped already. Its exit code tells the result.
        }
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process ended already.
        }
    }

    private async Task PumpStderrAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;
                lock (_stderrTail)
                {
                    if (_stderrTail.Count == StderrLineCount)
                        _stderrTail.Dequeue();
                    _stderrTail.Enqueue(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed when the process stopped.
        }
    }

    private static async Task DrainAsync(Stream output)
    {
        try
        {
            await output.CopyToAsync(Stream.Null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed when the process stopped.
        }
    }
}

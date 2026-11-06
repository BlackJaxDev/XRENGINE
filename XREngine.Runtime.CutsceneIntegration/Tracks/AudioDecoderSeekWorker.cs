using XREngine.Data;

namespace XREngine.Cutscenes;

/// <summary>
/// Seeks the full decoder of an audio track on a background task, so a slow seek (an Ogg Vorbis seek backward reads from
/// the start of the file) does not stop the update thread. The worker owns the decoder until <see cref="Close"/>.
/// </summary>
/// <remarks>
/// <para>
/// A decoder is not thread safe, so the update thread uses the decoder only while <see cref="TryGetIdle"/> is true.
/// Each <see cref="Request"/> gets a new generation. A newer request cancels an older request that did not start, and
/// makes the result of a running seek stale: the task seeks again to the newest frame before it reports idle.
/// </para>
/// <para>
/// <see cref="Close"/> cancels the work. If no seek runs, the decoder closes at once. Otherwise the task closes it at the
/// end of the running seek, so the decoder is never closed while a seek uses it.
/// </para>
/// </remarks>
internal sealed class AudioDecoderSeekWorker
{
    private static readonly Action<object?> RunAction = static state => ((AudioDecoderSeekWorker)state!).Run();

    private readonly Lock _sync = new();
    private readonly IStreamingAudioDecoder _decoder;
    private readonly TaskScheduler _scheduler;
    private readonly CancellationTokenSource _cancel = new();
    private int _requested;
    private long _requestFrame;
    private int _completed;
    private long _completedFrame;
    private Exception? _failure;
    private bool _running;

    /// <summary>Creates a worker that owns a decoder.</summary>
    /// <param name="decoder">The decoder. The worker closes it.</param>
    /// <param name="scheduler">The scheduler of the seek tasks.</param>
    public AudioDecoderSeekWorker(IStreamingAudioDecoder decoder, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(scheduler);
        _decoder = decoder;
        _scheduler = scheduler;
    }

    /// <summary>The number of seeks that the worker started. A request that a newer request cancelled does not start a seek.</summary>
    public int SeekCount { get; private set; }

    /// <summary>
    /// Asks for a seek to a frame. A newer request cancels a waiting request and makes a running result stale.
    /// The call starts a task only if no task runs.
    /// </summary>
    /// <param name="frame">The frame to seek to.</param>
    public void Request(long frame)
    {
        lock (_sync)
        {
            if (_cancel.IsCancellationRequested)
                return;
            _requested++;
            _requestFrame = frame;
            if (_running)
                return;
            _running = true;
        }

        // The task gets no cancellation token. A token that is cancelled before the start would skip the task, and the
        // decoder would then never close. The task reads the token itself.
        Task.Factory.StartNew(RunAction, this, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _scheduler);
    }

    /// <summary>
    /// True if no seek runs or waits, so the update thread can use the decoder. The decoder is then at the frame of the
    /// newest request, unless that seek failed.
    /// </summary>
    /// <param name="frame">The frame of the newest complete seek.</param>
    /// <param name="failure">The exception of the newest seek, or null if it succeeded.</param>
    public bool TryGetIdle(out long frame, out Exception? failure)
    {
        lock (_sync)
        {
            frame = _completedFrame;
            failure = _failure;
            return !_running && _completed == _requested;
        }
    }

    /// <summary>Cancels the work and closes the decoder now, or at the end of the running seek. A second call does nothing.</summary>
    public void Close()
    {
        bool closeNow;
        lock (_sync)
        {
            if (_cancel.IsCancellationRequested)
                return;
            _cancel.Cancel();
            closeNow = !_running;
        }

        if (closeNow)
            _decoder.Dispose();
    }

    private void Run()
    {
        CancellationToken token = _cancel.Token;
        while (true)
        {
            long frame;
            int generation;
            lock (_sync)
            {
                if (token.IsCancellationRequested || _completed == _requested)
                {
                    _running = false;
                    if (!token.IsCancellationRequested)
                        return;
                    break;
                }
                frame = _requestFrame;
                generation = _requested;
                SeekCount++;
            }

            Exception? failure = null;
            try
            {
                _decoder.SeekToFrame(frame);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }

            lock (_sync)
            {
                _completed = generation;
                _completedFrame = frame;
                _failure = failure;
            }
        }

        // Close ran while this task was running, so this task owns the close of the decoder.
        _decoder.Dispose();
    }
}

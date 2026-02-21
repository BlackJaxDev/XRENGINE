using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

internal sealed class HlsMediaStreamSession : IMediaStreamSession
{
    private readonly FFmpegStreamDecoder _decoder;
    private readonly object _sync = new();
    private readonly Queue<DecodedVideoFrame> _videoFrames = [];
    private readonly Queue<DecodedAudioFrame> _audioFrames = [];
    private int _videoQueueCapacity = 24;
    private int _audioQueueCapacity = 8;
    private long _latestQueuedVideoPts = long.MinValue;
    private bool _videoPlaybackStarted;
    private long _videoPlaybackBasePts;
    private long _videoPlaybackBaseWallTicks;
    private static readonly long TargetVideoBufferTicks = TimeSpan.FromMilliseconds(500).Ticks;
    private bool _isOpen;

    public HlsMediaStreamSession()
    {
        _decoder = new FFmpegStreamDecoder
        {
            OnVideoFrame = OnDecodedVideoFrame,
            OnAudioFrame = OnDecodedAudioFrame,
            OnVideoSizeChanged = (w, h) => VideoSizeChanged?.Invoke(w, h),
            OnError = err => Debug.UIWarning($"Stream decoder error: {err}")
        };
    }

    public event Action<int, int>? VideoSizeChanged;

    public bool IsOpen => _isOpen;

    public async Task OpenAsync(string url, StreamOpenOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (options is not null)
        {
            _videoQueueCapacity = Math.Max(1, options.VideoQueueCapacity);
            _audioQueueCapacity = Math.Max(1, options.AudioQueueCapacity);
        }

        await _decoder.OpenAsync(url, options, cancellationToken).ConfigureAwait(false);
        _isOpen = true;
    }

    public void SetTargetFramebuffer(uint framebufferId)
    {
        // No-op: FFmpeg decoder delivers CPU-side frames; GPU upload
        // is handled by IVideoFrameGpuActions after dequeue.
    }

    public void Present()
    {
        // No-op: the render loop drains frames via TryDequeueVideoFrame
        // and uploads them in DrainStreamingFramesOnMainThread.
    }

    public bool TryDequeueVideoFrame(out DecodedVideoFrame frame)
    {
        lock (_sync)
        {
            if (_videoFrames.Count == 0)
            {
                frame = default;
                return false;
            }

            DecodedVideoFrame candidate = _videoFrames.Peek();
            long candidatePts = candidate.PresentationTimestampTicks;
            bool hasValidPts = candidatePts > 0 && _latestQueuedVideoPts >= candidatePts;

            if (!_videoPlaybackStarted)
            {
                // If timestamps are missing/invalid, start immediately to avoid
                // getting stuck waiting for a jitter buffer that can never fill.
                if (!hasValidPts)
                {
                    _videoPlaybackStarted = true;
                    _videoPlaybackBasePts = 0;
                    _videoPlaybackBaseWallTicks = GetNowTicks();

                    frame = _videoFrames.Dequeue();
                    return true;
                }

                long oldestPts = candidatePts;
                long bufferedTicks = _latestQueuedVideoPts - oldestPts;

                // Build an initial jitter buffer before starting playout.
                if (bufferedTicks < TargetVideoBufferTicks && _videoFrames.Count < _videoQueueCapacity)
                {
                    frame = default;
                    return false;
                }

                _videoPlaybackStarted = true;
                _videoPlaybackBasePts = oldestPts;
                _videoPlaybackBaseWallTicks = GetNowTicks();
            }

            if (hasValidPts && _videoPlaybackBasePts > 0)
            {
                long dueTicks = _videoPlaybackBaseWallTicks + (candidatePts - _videoPlaybackBasePts);
                if (GetNowTicks() < dueTicks)
                {
                    frame = default;
                    return false;
                }
            }

            frame = _videoFrames.Dequeue();
            return true;
        }
    }

    public bool TryDequeueAudioFrame(out DecodedAudioFrame frame)
    {
        lock (_sync)
        {
            if (_audioFrames.Count == 0)
            {
                frame = default;
                return false;
            }

            frame = _audioFrames.Dequeue();
            return true;
        }
    }

    public void Close()
    {
        _decoder.Stop();
        lock (_sync)
        {
            _isOpen = false;
            _videoFrames.Clear();
            _audioFrames.Clear();
            _latestQueuedVideoPts = long.MinValue;
            _videoPlaybackStarted = false;
            _videoPlaybackBasePts = 0;
            _videoPlaybackBaseWallTicks = 0;
        }
    }

    public void Dispose()
    {
        _decoder.Dispose();
        _isOpen = false;
    }

    private void OnDecodedVideoFrame(DecodedVideoFrame frame)
    {
        lock (_sync)
        {
            if (!_isOpen)
                return;

            while (_videoFrames.Count >= _videoQueueCapacity)
                _videoFrames.Dequeue();

            _videoFrames.Enqueue(frame);
            if (frame.PresentationTimestampTicks > 0)
                _latestQueuedVideoPts = frame.PresentationTimestampTicks;
        }
    }

    private void OnDecodedAudioFrame(DecodedAudioFrame frame)
    {
        lock (_sync)
        {
            if (!_isOpen)
                return;

            while (_audioFrames.Count >= _audioQueueCapacity)
                _audioFrames.Dequeue();

            _audioFrames.Enqueue(frame);
        }
    }

    private static long GetNowTicks()
        => Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
}

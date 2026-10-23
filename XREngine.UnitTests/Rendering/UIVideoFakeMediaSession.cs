using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.UnitTests.Rendering;

internal sealed class UIVideoFakeMediaSession : IMediaStreamSession
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<int, int>? VideoSizeChanged { add { } remove { } }
    public bool IsOpen { get; private set; }
    public int QueuedVideoFrameCount => 0;
    public int QueuedAudioFrameCount => 0;
    public long EstimatedQueuedVideoDurationTicks => 0;
    public int OpenCount { get; private set; }
    public int CloseCount { get; private set; }
    public int DisposeCount { get; private set; }
    public CancellationToken OpenToken { get; private set; }
    public Action? OnClose { get; set; }
    public bool ThrowOnClose { get; set; }

    public Task OpenAsync(string url, StreamOpenOptions? options, CancellationToken cancellationToken)
    {
        OpenCount++;
        OpenToken = cancellationToken;
        return _open.Task;
    }

    public void CompleteOpen()
    {
        IsOpen = true;
        _open.TrySetResult();
    }

    public void FailOpen() => _open.TrySetException(new InvalidOperationException("Delayed fake open failed."));
    public void SetTargetFramebuffer(uint framebufferId) { }
    public void Present() { }
    public bool TryDequeueVideoFrame(long audioClockTicks, out DecodedVideoFrame frame)
    {
        frame = default;
        return false;
    }
    public bool TryDequeueAudioFrame(out DecodedAudioFrame frame)
    {
        frame = default;
        return false;
    }
    public void Close()
    {
        CloseCount++;
        IsOpen = false;
        OnClose?.Invoke();
        if (ThrowOnClose)
            throw new InvalidOperationException("Fake Close failed.");
    }
    public void Dispose() => DisposeCount++;
}

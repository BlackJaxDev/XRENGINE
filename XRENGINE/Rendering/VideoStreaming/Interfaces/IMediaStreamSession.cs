using System;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering.VideoStreaming.Interfaces;

public interface IMediaStreamSession : IDisposable
{
    event Action<int, int>? VideoSizeChanged;

    bool IsOpen { get; }

    /// <summary>Number of decoded video frames currently buffered in the session queue.</summary>
    int QueuedVideoFrameCount { get; }

    /// <summary>Number of decoded audio frames currently buffered in the session queue.</summary>
    int QueuedAudioFrameCount { get; }

    Task OpenAsync(string url, StreamOpenOptions? options, CancellationToken cancellationToken);
    void SetTargetFramebuffer(uint framebufferId);
    void Present();
    bool TryDequeueVideoFrame(long audioClockTicks, out DecodedVideoFrame frame);
    bool TryDequeueAudioFrame(out DecodedAudioFrame frame);
    void Close();
}

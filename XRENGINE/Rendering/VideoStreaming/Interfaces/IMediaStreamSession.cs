using System;
using System.Threading;
using System.Threading.Tasks;

namespace XREngine.Rendering.VideoStreaming.Interfaces;

public interface IMediaStreamSession : IDisposable
{
    event Action<int, int>? VideoSizeChanged;

    bool IsOpen { get; }
    Task OpenAsync(string url, StreamOpenOptions? options, CancellationToken cancellationToken);
    void SetTargetFramebuffer(uint framebufferId);
    void Present();
    bool TryDequeueVideoFrame(out DecodedVideoFrame frame);
    bool TryDequeueAudioFrame(out DecodedAudioFrame frame);
    void Close();
}

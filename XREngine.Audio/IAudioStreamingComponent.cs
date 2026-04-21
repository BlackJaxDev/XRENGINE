using System.Collections.Concurrent;

namespace XREngine.Audio;

/// <summary>
/// Minimal streaming surface needed by systems that drive an audio component without
/// taking a compile-time dependency on the concrete scene component assembly.
/// </summary>
public interface IAudioStreamingComponent
{
    ConcurrentDictionary<ListenerContext, AudioSource> ActiveListeners { get; }
    bool ExternalBufferManagement { get; set; }
    int MaxStreamingBuffers { get; set; }

    bool EnqueueStreamingBuffers(int frequency, bool stereo, params short[][] buffers);
    void DequeueConsumedBuffers();
}
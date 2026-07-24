namespace XREngine.Audio;

/// <summary>
/// Media-facing streaming surface implemented by audio integration components.
/// It deliberately hides listener contexts, OpenAL sources, and transport buffers.
/// </summary>
public interface IAudioStreamingComponent
{
    int ActiveListenerCount { get; }
    IAudioPlaybackSource? PrimaryPlaybackSource { get; }
    bool AnySourcePlaying { get; }
    int MinimumPlayableBufferCount { get; }
    bool ExternalBufferManagement { get; set; }
    int MaxStreamingBuffers { get; set; }

    bool EnqueueStreamingBuffers(int frequency, bool stereo, params short[][] buffers);
    void DequeueConsumedBuffers();
    bool RewindStoppedEmptySources();
    bool PlayStoppedSources();
    void StopAndRewindAllSources();
    void SetAutoPlayOnQueue(bool enabled);
}

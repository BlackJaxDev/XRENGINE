namespace XREngine.Audio;

/// <summary>
/// Runtime-neutral playback state needed by media presentation clocks.
/// </summary>
public interface IAudioPlaybackSource
{
    event Action? StreamingBufferProcessed;

    bool IsPlaying { get; }
    int SampleOffset { get; }
    int BuffersQueued { get; }
    int BuffersProcessed { get; }
}

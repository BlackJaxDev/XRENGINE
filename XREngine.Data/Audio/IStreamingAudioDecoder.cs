namespace XREngine.Data;

/// <summary>
/// Decodes an audio file in small parts, so the whole file never has to be in memory.
/// The decoder writes interleaved float samples in the range -1 to 1.
/// A decoder is not thread safe. Use one decoder on one thread at a time.
/// </summary>
public interface IStreamingAudioDecoder : IDisposable
{
    /// <summary>The sample rate of the file in frames per second.</summary>
    int SampleRate { get; }

    /// <summary>The number of channels in each frame.</summary>
    int Channels { get; }

    /// <summary>The number of frames in the file.</summary>
    long TotalFrames { get; }

    /// <summary>
    /// Moves the read position to a frame. The next <see cref="Read"/> starts at that frame.
    /// The value is limited to the range from zero to <see cref="TotalFrames"/>.
    /// </summary>
    /// <param name="frame">The frame index.</param>
    void SeekToFrame(long frame);

    /// <summary>
    /// Decodes frames at the read position and moves the position past them.
    /// The decoder writes whole frames only, so it ignores a part of the span that cannot hold a whole frame.
    /// </summary>
    /// <param name="destination">The span that receives the interleaved samples.</param>
    /// <returns>The number of frames written. Zero means the end of the file.</returns>
    int Read(Span<float> destination);
}

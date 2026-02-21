using System;

namespace XREngine.Rendering.VideoStreaming;

public readonly struct DecodedAudioFrame
{
    public DecodedAudioFrame(
        int sampleRate,
        int channelCount,
        AudioSampleFormat sampleFormat,
        long presentationTimestampTicks,
        ReadOnlyMemory<byte> interleavedData)
    {
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        SampleFormat = sampleFormat;
        PresentationTimestampTicks = presentationTimestampTicks;
        InterleavedData = interleavedData;
    }

    public int SampleRate { get; }
    public int ChannelCount { get; }
    public AudioSampleFormat SampleFormat { get; }
    public long PresentationTimestampTicks { get; }
    public ReadOnlyMemory<byte> InterleavedData { get; }
}

public enum AudioSampleFormat
{
    Unknown = 0,
    S16,
    S32,
    F32,
    F64
}

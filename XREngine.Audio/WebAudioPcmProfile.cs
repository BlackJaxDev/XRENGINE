using System.Buffers.Binary;

namespace XREngine.Audio;

/// <summary>Validates PCM that the browser audio transport can upload without conversion.</summary>
public static class WebAudioPcmProfile
{
    public const int MaximumBufferBytes = 8 * 1024 * 1024;

    /// <summary>Checks the format and complete interleaved frames before reading samples.</summary>
    public static int ValidateLayout(int byteLength, int frequency, int channels, SampleFormat format)
    {
        if (frequency is < 8000 or > 192000 || channels is not (1 or 2) ||
            format is not (SampleFormat.Byte or SampleFormat.Short or SampleFormat.Float))
            throw new NotSupportedException("WebAudio.PcmFormatUnsupported: requires mono/stereo 8-bit, 16-bit or float PCM at 8000 to 192000 Hz.");
        int bytesPerSample = format == SampleFormat.Byte ? 1 : format == SampleFormat.Short ? 2 : 4;
        int bytesPerFrame = channels * bytesPerSample;
        if (byteLength <= 0 || byteLength > MaximumBufferBytes || byteLength % bytesPerFrame != 0)
            throw new InvalidDataException("WebAudio.PcmBudgetExceeded: PCM must contain complete frames and fit the 8 MiB buffer limit.");
        return byteLength / bytesPerFrame;
    }

    /// <summary>Checks a borrowed PCM view, including finite little-endian float samples.</summary>
    public static void Validate(ReadOnlySpan<byte> pcm, int frequency, int channels, SampleFormat format)
    {
        ValidateLayout(pcm.Length, frequency, channels, format);
        if (format != SampleFormat.Float)
            return;
        for (int offset = 0; offset < pcm.Length; offset += sizeof(float))
            if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(pcm[offset..])))
                throw new InvalidDataException("WebAudio.PcmInvalid: samples must be finite.");
    }
}

namespace XREngine.Audio;

/// <summary>Host-provided codec operations used by voice conversion components.</summary>
public interface IAudioCodecServices
{
    byte[] DecodeMp3(byte[] encoded);
    byte[] ReadWavPcm(string filePath);
    void WriteWavPcm(string filePath, byte[] samples, int bitsPerSample, int sampleRate);
}

/// <summary>Explicit codec installation for desktop voice conversion.</summary>
public static class AudioCodecServices
{
    private static IAudioCodecServices? _current;

    public static IAudioCodecServices Current
    {
        get => _current ?? throw new InvalidOperationException("No audio codec service is registered.");
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }
}

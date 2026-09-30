using NAudio.Wave;

namespace XREngine.Audio;

/// <summary>NAudio codec services for desktop voice conversion.</summary>
public sealed class NAudioCodecServices : IAudioCodecServices
{
    public byte[] DecodeMp3(byte[] encoded)
    {
        using MemoryStream input = new(encoded, writable: false);
        using Mp3FileReader reader = new(input);
        return ReadAll(reader);
    }

    public byte[] ReadWavPcm(string filePath)
    {
        using WaveFileReader reader = new(filePath);
        return ReadAll(reader);
    }

    public void WriteWavPcm(string filePath, byte[] samples, int bitsPerSample, int sampleRate)
    {
        using WaveFileWriter writer = new(filePath, new WaveFormat(sampleRate, bitsPerSample, 1));
        writer.Write(samples, 0, samples.Length);
        writer.Flush();
    }

    private static byte[] ReadAll(WaveStream stream)
    {
        if (stream.Length > int.MaxValue)
            throw new InvalidDataException("Decoded audio exceeds the maximum supported buffer size.");
        byte[] samples = new byte[(int)stream.Length];
        stream.ReadExactly(samples);
        return samples;
    }
}

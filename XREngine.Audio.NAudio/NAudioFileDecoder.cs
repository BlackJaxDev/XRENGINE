using NAudio.Wave;
using XREngine.Data;

namespace XREngine.Audio;

/// <summary>NAudio-backed WAV and MP3 file decoder for desktop asset imports.</summary>
public sealed class NAudioFileDecoder : IAudioFileDecoder
{
    public DecodedAudioFile DecodeMp3(string filePath)
    {
        using Mp3FileReader reader = new(filePath);
        using WaveStream pcmStream = WaveFormatConversionStream.CreatePcmStream(reader);
        byte[] samples = ReadAll(pcmStream);
        return new DecodedAudioFile(samples, AudioData.EPCMType.Short,
            pcmStream.WaveFormat.SampleRate, pcmStream.WaveFormat.Channels);
    }

    public DecodedAudioFile DecodeWav(string filePath)
    {
        using FileStream file = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using WaveFileReader reader = new(file);
        byte[] samples = ReadAll(reader);
        AudioData.EPCMType type = reader.WaveFormat.BitsPerSample switch
        {
            8 => AudioData.EPCMType.Byte,
            16 => AudioData.EPCMType.Short,
            32 => AudioData.EPCMType.Float,
            _ => AudioData.EPCMType.Float,
        };
        if (reader.WaveFormat.BitsPerSample is not (8 or 16 or 32))
        {
            float[] converted = AudioData.ConvertToFloat(samples, reader.WaveFormat.BitsPerSample);
            samples = System.Runtime.InteropServices.MemoryMarshal.AsBytes(converted.AsSpan()).ToArray();
        }

        return new DecodedAudioFile(samples, type,
            reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
    }

    private static byte[] ReadAll(WaveStream stream)
    {
        if (stream.Length > int.MaxValue)
            throw new InvalidDataException("Decoded audio exceeds the maximum supported asset size.");

        byte[] samples = new byte[(int)stream.Length];
        int offset = 0;
        while (offset < samples.Length)
        {
            int read = stream.Read(samples, offset, samples.Length - offset);
            if (read == 0)
                throw new EndOfStreamException(
                    $"Audio stream ended early: expected {samples.Length} bytes, got {offset} bytes.");
            offset += read;
        }

        return samples;
    }
}

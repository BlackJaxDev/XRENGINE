namespace XREngine.Data;

/// <summary>
/// Decodes a supported audio file into interleaved PCM for an <see cref="AudioData"/> asset.
/// Desktop hosts register their decoder before importing compressed or WAV audio.
/// </summary>
public interface IAudioFileDecoder
{
    DecodedAudioFile DecodeMp3(string filePath);
    DecodedAudioFile DecodeWav(string filePath);
}

/// <summary>Decoded interleaved PCM samples and their playback format.</summary>
public readonly record struct DecodedAudioFile(
    byte[] Samples,
    AudioData.EPCMType Type,
    int Frequency,
    int ChannelCount);

using XREngine.Audio;
using XREngine.Data;

namespace XREngine.Editor.Publishing;

/// <summary>Admits decoded PCM assets for browser cooking without changing authored samples.</summary>
internal static class BrowserAudioCookProfile
{
    internal static unsafe void Validate(AudioData audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        try
        {
            DataSource data = audio.Data
                ?? throw new InvalidDataException("Audio data has no PCM payload.");
            SampleFormat format = audio.Type switch
            {
                AudioData.EPCMType.Byte => SampleFormat.Byte,
                AudioData.EPCMType.Short => SampleFormat.Short,
                AudioData.EPCMType.Float => SampleFormat.Float,
                _ => throw new NotSupportedException("Unknown PCM sample type."),
            };
            int length = checked((int)data.Length);
            WebAudioPcmProfile.ValidateLayout(length, audio.Frequency, audio.ChannelCount, format);
            if (data.Address.Pointer is null)
                throw new InvalidDataException("Audio PCM storage is unavailable.");
            WebAudioPcmProfile.Validate(new ReadOnlySpan<byte>(data.Address.Pointer, length),
                audio.Frequency, audio.ChannelCount, format);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
        {
            throw new NotSupportedException($"BrowserCook.AudioPcmUnsupported: audio '{audio.Name}' ({audio.ID}): {error.Message}", error);
        }
    }
}

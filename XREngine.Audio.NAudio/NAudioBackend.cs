using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Audio;

/// <summary>Installs NAudio transport and file decoding for a desktop host.</summary>
public static class NAudioBackend
{
    public static void Register()
    {
        AudioBackendRegistry.RegisterTransport(EAudioTransport.NAudio, CreateTransport);
        AudioCaptureRegistry.Register(
            static (deviceIndex, sampleRate, bitsPerSample, bufferMilliseconds) =>
                new NAudioCaptureStream(deviceIndex, sampleRate, bitsPerSample, bufferMilliseconds),
            NAudioCaptureStream.GetDeviceNames,
            NAudioCaptureStream.GetAsioDriverNames);
        AudioData.FileDecoder = new NAudioFileDecoder();
        AudioCodecServices.Current = new NAudioCodecServices();
    }

    private static IAudioTransport CreateTransport()
    {
        var transport = new NAudioTransport();
        transport.Open();
        return transport;
    }
}

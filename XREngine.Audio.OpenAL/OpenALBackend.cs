namespace XREngine.Audio;

/// <summary>Installs the desktop OpenAL listener and transport backend explicitly.</summary>
public static class OpenALBackend
{
    public static void Register()
    {
        AudioBackendRegistry.RegisterTransport(EAudioTransport.OpenAL, static () => new OpenALTransport());
        AudioBackendRegistry.RegisterEffects(EAudioEffects.OpenAL_EFX, static transport =>
            transport is OpenALTransport openAl
                ? new OpenALEfxProcessor(openAl)
                : throw new InvalidOperationException("OpenAL EFX requires an OpenAL transport."));
    }
}

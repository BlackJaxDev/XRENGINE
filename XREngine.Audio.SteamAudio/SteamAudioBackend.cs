using XREngine.Data.Core;
using XREngine.Audio.Steam;

namespace XREngine.Audio;

/// <summary>Installs Steam Audio effects in a desktop host.</summary>
public static class SteamAudioBackend
{
    public static void Register()
        => AudioBackendRegistry.RegisterEffects(EAudioEffects.SteamAudio,
            static _ => new SteamAudioProcessor());
}

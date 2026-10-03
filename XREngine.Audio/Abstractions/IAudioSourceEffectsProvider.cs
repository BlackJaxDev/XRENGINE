namespace XREngine.Audio;

/// <summary>Optional source effects factory implemented by a capable transport.</summary>
public interface IAudioSourceEffectsProvider
{
    IAudioSourceEffects? CreateSourceEffects(ListenerContext listener);
}

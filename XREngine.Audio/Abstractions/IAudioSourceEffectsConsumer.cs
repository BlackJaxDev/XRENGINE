namespace XREngine.Audio;

/// <summary>Receives the listener-owned native source effects context.</summary>
public interface IAudioSourceEffectsConsumer
{
    void SetSourceEffects(IAudioSourceEffects? effects);
}

namespace XREngine.Audio;

/// <summary>Optional native source effects bound to one listener context.</summary>
public interface IAudioSourceEffects : IDisposable
{
    ListenerContext Listener { get; }
    int GetSourceProperty(AudioSourceHandle source, AudioEffectIntegerProperty property);
    void SetSourceProperty(AudioSourceHandle source, AudioEffectIntegerProperty property, int value);
    float GetSourceProperty(AudioSourceHandle source, AudioEffectFloatProperty property);
    void SetSourceProperty(AudioSourceHandle source, AudioEffectFloatProperty property, float value);
    bool GetSourceProperty(AudioSourceHandle source, AudioEffectBooleanProperty property);
    void SetSourceProperty(AudioSourceHandle source, AudioEffectBooleanProperty property, bool value);
    void GetSourceProperty(AudioSourceHandle source, AudioEffectTripleProperty property, out int x, out int y, out int z);
    void SetSourceProperty(AudioSourceHandle source, AudioEffectTripleProperty property, int x, int y, int z);
}

using System.Numerics;

namespace XREngine.Audio;

/// <summary>
/// Supplies the state and spatial controls required by the legacy listener and source facades.
/// A transport that cannot provide these controls must not be selected for legacy playback.
/// </summary>
public interface IAudioListenerBackend : IAudioTransport
{
    void MakeCurrent();
    void VerifyError();

    Vector3 GetListenerPosition();
    Vector3 GetListenerVelocity();
    void GetListenerOrientation(out Vector3 forward, out Vector3 up);
    float GetListenerGain();

    float GetDopplerFactor();
    void SetDopplerFactor(float factor);
    float GetSpeedOfSound();
    void SetSpeedOfSound(float speed);
    EDistanceModel GetDistanceModel();
    void SetDistanceModel(EDistanceModel model);

    bool IsExtensionPresent(string extension);
    bool HasDopplerFactorSet();
    bool HasDopplerVelocitySet();
    bool HasSpeedOfSoundSet();
    bool IsDistanceModelInverseDistanceClamped();
    string GetVendor();
    string GetRenderer();
    string GetVersion();
    string[] GetExtensions();

    AudioSource.ESourceState GetSourceState(AudioSourceHandle source);
    AudioSource.ESourceType GetSourceType(AudioSourceHandle source);
    AudioBufferHandle GetSourceBuffer(AudioSourceHandle source);
    float GetSourceProperty(AudioSourceHandle source, AudioSourceFloatProperty property);
    void SetSourceProperty(AudioSourceHandle source, AudioSourceFloatProperty property, float value);
    int GetSourceProperty(AudioSourceHandle source, AudioSourceIntegerProperty property);
    void SetSourceProperty(AudioSourceHandle source, AudioSourceIntegerProperty property, int value);
    bool GetSourceProperty(AudioSourceHandle source, AudioSourceBooleanProperty property);
    void SetSourceProperty(AudioSourceHandle source, AudioSourceBooleanProperty property, bool value);
    Vector3 GetSourceProperty(AudioSourceHandle source, AudioSourceVectorProperty property);
    void SetSourceProperty(AudioSourceHandle source, AudioSourceVectorProperty property, Vector3 value);
}

using System.Numerics;

namespace XREngine.Audio;

/// <summary>
/// Extended authored spatial and seek controls for composed transports. Source gain bounds
/// apply after distance/cone attenuation and before listener gain; stereo remains nonspatial.
/// </summary>
public interface IAudioSpatialTransport : IAudioTransport
{
    float GetDopplerFactor();
    void SetDopplerFactor(float factor);
    float GetSpeedOfSound();
    void SetSpeedOfSound(float speed);
    EDistanceModel GetDistanceModel();
    void SetDistanceModel(EDistanceModel model);

    /// <summary>Reads an authored scalar; SecondsOffset is measured from the retained queue's beginning.</summary>
    float GetSourceProperty(AudioSourceHandle source, AudioSourceFloatProperty property);
    /// <summary>Reads a PCM byte or sample-frame offset from the retained queue's beginning.</summary>
    int GetSourceProperty(AudioSourceHandle source, AudioSourceIntegerProperty property);
    /// <summary>
    /// Sets an authored scalar. SecondsOffset seeks from the retained queue's beginning,
    /// marks traversed entries processed, and preserves paused playback until Play.
    /// </summary>
    void SetSourceProperty(AudioSourceHandle source, AudioSourceFloatProperty property, float value);
    /// <summary>
    /// Seeks from the retained queue's beginning, marks traversed entries processed, and
    /// preserves paused playback until Play. This differs from the current-buffer precision
    /// returned by <see cref="IAudioTransport.GetSampleOffset"/>.
    /// </summary>
    void SetSourceProperty(AudioSourceHandle source, AudioSourceIntegerProperty property, int value);
    void SetSourceProperty(AudioSourceHandle source, AudioSourceBooleanProperty property, bool value);
    void SetSourceProperty(AudioSourceHandle source, AudioSourceVectorProperty property, Vector3 value);
}

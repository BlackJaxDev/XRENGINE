using System.Numerics;

namespace XREngine.Audio.WebAudio;

public sealed partial class WebAudioTransport
{
    public float GetDopplerFactor() => (float)WebAudioImports.ListenerProperty(RequireOpen(), 0);
    public void SetDopplerFactor(float factor) => WebAudioImports.SetListenerProperty(RequireOpen(), 0, factor);
    public float GetSpeedOfSound() => (float)WebAudioImports.ListenerProperty(RequireOpen(), 1);
    public void SetSpeedOfSound(float speed) => WebAudioImports.SetListenerProperty(RequireOpen(), 1, speed);
    public EDistanceModel GetDistanceModel() => (EDistanceModel)(int)WebAudioImports.ListenerProperty(RequireOpen(), 2);
    public void SetDistanceModel(EDistanceModel model) => WebAudioImports.SetListenerProperty(RequireOpen(), 2, (int)model);

    public float GetSourceProperty(AudioSourceHandle source, AudioSourceFloatProperty property)
        => (float)WebAudioImports.SourceFloatProperty(RequireOpen(), checked((int)source.Id), (int)property);
    public int GetSourceProperty(AudioSourceHandle source, AudioSourceIntegerProperty property)
        => checked((int)WebAudioImports.SourceQueueOffset(RequireOpen(), checked((int)source.Id), OffsetUnit(property)));
    public void SetSourceProperty(AudioSourceHandle source, AudioSourceFloatProperty property, float value)
        => WebAudioImports.SetSourceFloatProperty(RequireOpen(), checked((int)source.Id), (int)property, value);
    public void SetSourceProperty(AudioSourceHandle source, AudioSourceIntegerProperty property, int value)
        => WebAudioImports.SeekSource(RequireOpen(), checked((int)source.Id), OffsetUnit(property), value);
    public void SetSourceProperty(AudioSourceHandle source, AudioSourceBooleanProperty property, bool value)
    {
        switch (property)
        {
            case AudioSourceBooleanProperty.RelativeToListener:
                WebAudioImports.SourceRelative(RequireOpen(), checked((int)source.Id), value);
                break;
            case AudioSourceBooleanProperty.Looping:
                SetSourceLooping(source, value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown source boolean property.");
        }
    }
    public void SetSourceProperty(AudioSourceHandle source, AudioSourceVectorProperty property, Vector3 value)
    {
        switch (property)
        {
            case AudioSourceVectorProperty.Position:
                SetSourcePosition(source, value);
                break;
            case AudioSourceVectorProperty.Velocity:
                SetSourceVelocity(source, value);
                break;
            case AudioSourceVectorProperty.Direction:
                WebAudioImports.SourceDirection(RequireOpen(), checked((int)source.Id), value.X, value.Y, value.Z);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown source vector property.");
        }
    }

    private static int OffsetUnit(AudioSourceIntegerProperty property) => property switch
    {
        AudioSourceIntegerProperty.ByteOffset => 2,
        AudioSourceIntegerProperty.SampleOffset => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown source offset property."),
    };
}

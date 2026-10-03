using Silk.NET.OpenAL;
using System.Numerics;

namespace XREngine.Audio;

public sealed unsafe partial class OpenALTransport
{
    Vector3 IAudioListenerBackend.GetListenerPosition() => GetListenerPosition();
    Vector3 IAudioListenerBackend.GetListenerVelocity() => GetListenerVelocity();
    void IAudioListenerBackend.GetListenerOrientation(out Vector3 forward, out Vector3 up)
        => GetListenerOrientation(out forward, out up);
    float IAudioListenerBackend.GetListenerGain() => GetListenerGain();
    float IAudioListenerBackend.GetDopplerFactor() => GetDopplerFactor();
    void IAudioListenerBackend.SetDopplerFactor(float factor) => SetDopplerFactor(factor);
    float IAudioListenerBackend.GetSpeedOfSound() => GetSpeedOfSound();
    void IAudioListenerBackend.SetSpeedOfSound(float speed) => SetSpeedOfSound(speed);
    EDistanceModel IAudioListenerBackend.GetDistanceModel() => (EDistanceModel)GetDistanceModel();
    void IAudioListenerBackend.SetDistanceModel(EDistanceModel model) => SetDistanceModel((DistanceModel)model);
    bool IAudioListenerBackend.IsExtensionPresent(string extension) => IsExtensionPresent(extension);
    bool IAudioListenerBackend.HasDopplerFactorSet() => GetStateBoolean(StateBoolean.HasDopplerFactor);
    bool IAudioListenerBackend.HasDopplerVelocitySet() => GetStateBoolean(StateBoolean.HasDopplerVelocity);
    bool IAudioListenerBackend.HasSpeedOfSoundSet() => GetStateBoolean(StateBoolean.HasSpeedOfSound);
    bool IAudioListenerBackend.IsDistanceModelInverseDistanceClamped()
        => GetStateBoolean(StateBoolean.IsDistanceModelInverseDistanceClamped);
    string IAudioListenerBackend.GetVendor() => GetVendor();
    string IAudioListenerBackend.GetRenderer() => GetRenderer();
    string IAudioListenerBackend.GetVersion() => GetVersion();
    string[] IAudioListenerBackend.GetExtensions() => GetExtensions();

    private bool GetStateBoolean(StateBoolean property)
    {
            using var native = EnterNative();
        bool value = Api.GetStateProperty(property);
        VerifyError();
        return value;
    }

    public AudioSource.ESourceState GetSourceState(AudioSourceHandle source)
    {
        SourceState state = (SourceState)GetSourcePropertyInt(source.Id, GetSourceInteger.SourceState);
        return state switch
        {
            SourceState.Initial => AudioSource.ESourceState.Initial,
            SourceState.Playing => AudioSource.ESourceState.Playing,
            SourceState.Paused => AudioSource.ESourceState.Paused,
            SourceState.Stopped => AudioSource.ESourceState.Stopped,
            _ => throw new InvalidOperationException($"OpenAL returned unknown source state '{state}'."),
        };
    }

    public AudioSource.ESourceType GetSourceType(AudioSourceHandle source)
    {
        SourceType type = (SourceType)GetSourcePropertyInt(source.Id, GetSourceInteger.SourceType);
        return type switch
        {
            SourceType.Static => AudioSource.ESourceType.Static,
            SourceType.Streaming => AudioSource.ESourceType.Streaming,
            SourceType.Undetermined => AudioSource.ESourceType.Undetermined,
            _ => throw new InvalidOperationException($"OpenAL returned unknown source type '{type}'."),
        };
    }

    public AudioBufferHandle GetSourceBuffer(AudioSourceHandle source)
        => new((uint)GetSourcePropertyInt(source.Id, GetSourceInteger.Buffer));

    public float GetSourceProperty(AudioSourceHandle source, AudioSourceFloatProperty property)
    {
            using var native = EnterNative();
        Api.GetSourceProperty(source.Id, Map(property), out float value);
        VerifyError();
        return value;
    }

    public void SetSourceProperty(AudioSourceHandle source, AudioSourceFloatProperty property, float value)
    {
            using var native = EnterNative();
        Api.SetSourceProperty(source.Id, Map(property), value);
        VerifyError();
    }

    public int GetSourceProperty(AudioSourceHandle source, AudioSourceIntegerProperty property)
    {
            using var native = EnterNative();
        return GetSourcePropertyInt(source.Id, MapGet(property));
    }

    public void SetSourceProperty(AudioSourceHandle source, AudioSourceIntegerProperty property, int value)
    {
            using var native = EnterNative();
        Api.SetSourceProperty(source.Id, MapSet(property), value);
        VerifyError();
    }

    public bool GetSourceProperty(AudioSourceHandle source, AudioSourceBooleanProperty property)
    {
            using var native = EnterNative();
        Api.GetSourceProperty(source.Id, Map(property), out bool value);
        VerifyError();
        return value;
    }

    public void SetSourceProperty(AudioSourceHandle source, AudioSourceBooleanProperty property, bool value)
    {
            using var native = EnterNative();
        Api.SetSourceProperty(source.Id, Map(property), value);
        VerifyError();
    }

    public Vector3 GetSourceProperty(AudioSourceHandle source, AudioSourceVectorProperty property)
    {
            using var native = EnterNative();
        Api.GetSourceProperty(source.Id, Map(property), out Vector3 value);
        VerifyError();
        return value;
    }

    public void SetSourceProperty(AudioSourceHandle source, AudioSourceVectorProperty property, Vector3 value)
    {
            using var native = EnterNative();
        Api.SetSourceProperty(source.Id, Map(property), value);
        VerifyError();
    }

    private static SourceFloat Map(AudioSourceFloatProperty property) => property switch
    {
        AudioSourceFloatProperty.ReferenceDistance => SourceFloat.ReferenceDistance,
        AudioSourceFloatProperty.MaxDistance => SourceFloat.MaxDistance,
        AudioSourceFloatProperty.RolloffFactor => SourceFloat.RolloffFactor,
        AudioSourceFloatProperty.Pitch => SourceFloat.Pitch,
        AudioSourceFloatProperty.MinGain => SourceFloat.MinGain,
        AudioSourceFloatProperty.MaxGain => SourceFloat.MaxGain,
        AudioSourceFloatProperty.Gain => SourceFloat.Gain,
        AudioSourceFloatProperty.ConeInnerAngle => SourceFloat.ConeInnerAngle,
        AudioSourceFloatProperty.ConeOuterAngle => SourceFloat.ConeOuterAngle,
        AudioSourceFloatProperty.ConeOuterGain => SourceFloat.ConeOuterGain,
        AudioSourceFloatProperty.SecondsOffset => SourceFloat.SecOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
    };

    private static GetSourceInteger MapGet(AudioSourceIntegerProperty property) => property switch
    {
        AudioSourceIntegerProperty.ByteOffset => GetSourceInteger.ByteOffset,
        AudioSourceIntegerProperty.SampleOffset => GetSourceInteger.SampleOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
    };

    private static SourceInteger MapSet(AudioSourceIntegerProperty property) => property switch
    {
        AudioSourceIntegerProperty.ByteOffset => SourceInteger.ByteOffset,
        AudioSourceIntegerProperty.SampleOffset => SourceInteger.SampleOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
    };

    private static SourceBoolean Map(AudioSourceBooleanProperty property) => property switch
    {
        AudioSourceBooleanProperty.RelativeToListener => SourceBoolean.SourceRelative,
        AudioSourceBooleanProperty.Looping => SourceBoolean.Looping,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
    };

    private static SourceVector3 Map(AudioSourceVectorProperty property) => property switch
    {
        AudioSourceVectorProperty.Position => SourceVector3.Position,
        AudioSourceVectorProperty.Velocity => SourceVector3.Velocity,
        AudioSourceVectorProperty.Direction => SourceVector3.Direction,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
    };
}

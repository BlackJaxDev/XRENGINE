using System.Numerics;
using MemoryPack;
using XREngine.Animation;
using XREngine.Components.Animation;

namespace XREngine;

/// <summary>Converts built-in property animations to closed, versioned runtime payloads.</summary>
internal static partial class PublishedAnimationPropertyCodec
{
    private const byte Version = 1;

    private enum AnimationKind : byte
    {
        Bool = 1,
        Int = 2,
        Float = 3,
        Vector2 = 4,
        Vector3 = 5,
        Vector4 = 6,
        Quaternion = 7,
        Matrix = 8,
        String = 9,
        Object = 10,
        AnimationCurve = 11
    }

    public static SerializedPropertyAnimationModel? CreateModel(BasePropAnim? animation, string context)
    {
        if (animation is null)
            return null;

        PublishedAnimationPropertyModel state = animation.GetType() switch
        {
            var type when type == typeof(PropAnimBool) => CaptureBool((PropAnimBool)animation, context),
            var type when type == typeof(PropAnimInt) => CaptureVector((PropAnimInt)animation, AnimationKind.Int, context),
            var type when type == typeof(PropAnimFloat) => CaptureVector((PropAnimFloat)animation, AnimationKind.Float, context),
            var type when type == typeof(PropAnimVector2) => CaptureVector((PropAnimVector2)animation, AnimationKind.Vector2, context),
            var type when type == typeof(PropAnimVector3) => CaptureVector((PropAnimVector3)animation, AnimationKind.Vector3, context),
            var type when type == typeof(PropAnimVector4) => CaptureVector((PropAnimVector4)animation, AnimationKind.Vector4, context),
            var type when type == typeof(PropAnimQuaternion) => CaptureQuaternion((PropAnimQuaternion)animation, context),
            var type when type == typeof(PropAnimMatrix) => CaptureMatrix((PropAnimMatrix)animation, context),
            var type when type == typeof(PropAnimString) => CaptureString((PropAnimString)animation, context),
            var type when type == typeof(PropAnimObject) => CaptureObject((PropAnimObject)animation, context),
            var type when type == typeof(AnimationCurve) => CaptureVector((AnimationCurve)animation, AnimationKind.AnimationCurve, context),
            _ => throw new NotSupportedException($"Published animation at '{context}' has unsupported type '{animation.GetType().FullName}'.")
        };

        byte[] payload = MemoryPackSerializer.Serialize(state);
        byte[] encoded = new byte[payload.Length + 6];
        encoded[0] = (byte)'X';
        encoded[1] = (byte)'A';
        encoded[2] = (byte)'P';
        encoded[3] = (byte)'R';
        encoded[4] = Version;
        encoded[5] = state.Kind;
        payload.AsSpan().CopyTo(encoded.AsSpan(6));
        return new SerializedPropertyAnimationModel
        {
            TypeName = animation.GetType().FullName,
            LengthInSeconds = animation.LengthInSeconds,
            Payload = encoded
        };
    }

    public static BasePropAnim? CreateRuntimeAnimation(SerializedPropertyAnimationModel? model, string context)
    {
        if (model is null)
            return null;

        byte[] payload = model.Payload ?? throw new InvalidDataException($"Published animation at '{context}' has no payload.");
        if (payload.Length < 6 || payload[0] != (byte)'X' || payload[1] != (byte)'A'
            || payload[2] != (byte)'P' || payload[3] != (byte)'R')
            throw new InvalidDataException($"Published animation at '{context}' has an invalid payload header.");
        if (payload[4] != Version)
            throw new InvalidDataException($"Published animation at '{context}' has unsupported version {payload[4]}.");

        PublishedAnimationPropertyModel state = MemoryPackSerializer.Deserialize<PublishedAnimationPropertyModel>(payload.AsSpan(6))
            ?? throw new InvalidDataException($"Published animation at '{context}' has an empty payload.");
        if (state.Kind != payload[5])
            throw new InvalidDataException($"Published animation at '{context}' has conflicting kind tags.");

        return (AnimationKind)state.Kind switch
        {
            AnimationKind.Bool => RestoreBool(state, context),
            AnimationKind.Int => RestoreVector<int, IntKeyframe, PropAnimInt>(state, context),
            AnimationKind.Float => RestoreVector<float, FloatKeyframe, PropAnimFloat>(state, context),
            AnimationKind.Vector2 => RestoreVector<Vector2, Vector2Keyframe, PropAnimVector2>(state, context),
            AnimationKind.Vector3 => RestoreVector<Vector3, Vector3Keyframe, PropAnimVector3>(state, context),
            AnimationKind.Vector4 => RestoreVector<Vector4, Vector4Keyframe, PropAnimVector4>(state, context),
            AnimationKind.Quaternion => RestoreQuaternion(state, context),
            AnimationKind.Matrix => RestoreMatrix(state, context),
            AnimationKind.String => RestoreString(state, context),
            AnimationKind.Object => RestoreObject(state, context),
            AnimationKind.AnimationCurve => RestoreVector<float, FloatKeyframe, AnimationCurve>(state, context),
            _ => throw new InvalidDataException($"Published animation at '{context}' has unsupported kind {state.Kind}.")
        };
    }

    private static PublishedAnimationPropertyModel CaptureCommon(BasePropAnimBakeable animation, BaseKeyframeTrack track, AnimationKind kind)
        => new()
        {
            Kind = (byte)kind,
            LengthInSeconds = animation.LengthInSeconds,
            Looped = animation.Looped,
            Speed = animation.Speed,
            AuthoredFrameCount = animation.AuthoredFrameCount,
            AuthoredFramesPerSecond = animation.AuthoredFramesPerSecond,
            BakedFramesPerSecond = animation.BakedFramesPerSecond,
            BakedFrameCount = animation.BakedFrameCount,
            RequestedCompression = animation.BakedValueCompressionAlgorithm,
            EncodedCompression = animation.EncodedBakedValueCompressionAlgorithm,
            IsBaked = animation.IsBaked,
            PreInfinityMode = track.PreInfinityMode,
            PostInfinityMode = track.PostInfinityMode
        };

    private static void RestoreCommon(BasePropAnimBakeable animation, BaseKeyframeTrack track, PublishedAnimationPropertyModel state)
    {
        if (state.AuthoredFramesPerSecond > 0 && state.AuthoredFrameCount > 0)
            animation.SetAuthoredCadence(new AuthoredCadence(state.AuthoredFrameCount, state.AuthoredFramesPerSecond), notifyChanged: false);
        animation.LengthInSeconds = state.LengthInSeconds;
        animation.Looped = state.Looped;
        animation.Speed = state.Speed;
        track.PreInfinityMode = state.PreInfinityMode;
        track.PostInfinityMode = state.PostInfinityMode;
    }

    private static void RestoreBakedState(BasePropAnimBakeable animation, PublishedAnimationPropertyModel state)
        => animation.RestorePublishedBakedState(
            state.BakedFramesPerSecond, state.BakedFrameCount,
            state.RequestedCompression, state.EncodedCompression, state.IsBaked);

    private static byte[][]? CaptureSamples<T>(T[] values, bool isBaked, string context)
    {
        if (!isBaked)
            return null;
        byte[][] encoded = new byte[values.Length][];
        for (int i = 0; i < values.Length; i++)
            encoded[i] = PublishedAnimationValueCodec.Encode(values[i], $"{context}.baked[{i}]");
        return encoded;
    }

    private static T[] RestoreSamples<T>(PublishedAnimationPropertyModel state, string context)
    {
        byte[][] encoded = state.BakedValues
            ?? throw new InvalidDataException($"Published animation at '{context}' is missing baked samples.");
        if (state.IsBaked && encoded.Length != state.BakedFrameCount)
            throw new InvalidDataException($"Published animation at '{context}' has {encoded.Length} baked samples but declares {state.BakedFrameCount} frames.");
        T[] values = new T[encoded.Length];
        for (int i = 0; i < encoded.Length; i++)
            values[i] = Required<T>(encoded[i], $"{context}.baked[{i}]");
        return values;
    }

    private static T Required<T>(byte[]? encoded, string context)
    {
        object? value = PublishedAnimationValueCodec.Decode(encoded, context);
        return value is T typed
            ? typed
            : throw new InvalidDataException($"Published animation value at '{context}' is not '{typeof(T).FullName}'.");
    }

    private static void ValidateKeys(PublishedAnimationPropertyModel state, string context)
    {
        if (state.Keyframes is null)
            throw new InvalidDataException($"Published animation at '{context}' has no keyframe list.");
    }
}

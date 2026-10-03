using System.Numerics;
using XREngine.Animation;

namespace XREngine;

internal static partial class PublishedAnimationPropertyCodec
{
    private static PublishedAnimationPropertyModel CaptureStep<TKeyframe, TValue>(
        BasePropAnimBakeable animation, KeyframeTrack<TKeyframe> track, AnimationKind kind,
        TValue defaultValue, TValue[] bakedValues, Func<TKeyframe, TValue> getValue, string context)
        where TKeyframe : Keyframe, new()
    {
        PublishedAnimationPropertyModel state = CaptureCommon(animation, track, kind);
        state.DefaultValue = PublishedAnimationValueCodec.Encode(defaultValue, $"{context}.default");
        state.BakedValues = CaptureSamples(bakedValues, animation.IsBaked, context);
        foreach (TKeyframe key in track)
        {
            string keyContext = $"{context}.key[{state.Keyframes.Count}]";
            state.Keyframes.Add(new PublishedAnimationKeyframeModel
            {
                Second = key.Second,
                AuthoredFrameIndex = key.AuthoredFrameIndex,
                Value = PublishedAnimationValueCodec.Encode(getValue(key), $"{keyContext}.value")
            });
        }
        return state;
    }

    private static void RestoreStepKeys<TKeyframe, TValue>(
        KeyframeTrack<TKeyframe> track, PublishedAnimationPropertyModel state,
        Action<TKeyframe, TValue> setValue, string context)
        where TKeyframe : Keyframe, new()
    {
        for (int i = 0; i < state.Keyframes.Count; i++)
        {
            PublishedAnimationKeyframeModel source = state.Keyframes[i];
            string keyContext = $"{context}.key[{i}].value";
            object? value = PublishedAnimationValueCodec.Decode(source.Value, keyContext);
            if (value is not TValue && (value is not null || typeof(TValue).IsValueType))
                throw new InvalidDataException($"Published animation value at '{keyContext}' is not '{typeof(TValue).FullName}'.");
            TKeyframe key = new() { Second = source.Second, AuthoredFrameIndex = source.AuthoredFrameIndex };
            setValue(key, (TValue)value!);
            track.Add(key);
        }
    }

    private static PublishedAnimationPropertyModel CaptureBool(PropAnimBool animation, string context)
        => CaptureStep(animation, animation.Keyframes, AnimationKind.Bool, animation.DefaultValue,
            animation.CapturePublishedBakedValues(), static key => key.Value, context);

    private static PropAnimBool RestoreBool(PublishedAnimationPropertyModel state, string context)
    {
        ValidateKeys(state, context);
        PropAnimBool animation = new();
        RestoreCommon(animation, animation.Keyframes, state);
        animation.DefaultValue = Required<bool>(state.DefaultValue, $"{context}.default");
        RestoreStepKeys<BoolKeyframe, bool>(animation.Keyframes, state, static (key, value) => key.Value = value, context);
        if (state.IsBaked)
            animation.RestorePublishedBakedValues(RestoreSamples<bool>(state, context), state.EncodedCompression);
        RestoreBakedState(animation, state);
        return animation;
    }

    private static PublishedAnimationPropertyModel CaptureString(PropAnimString animation, string context)
        => CaptureStep(animation, animation.Keyframes, AnimationKind.String, animation.DefaultValue,
            animation.CapturePublishedBakedValues(), static key => key.Value, context);

    private static PropAnimString RestoreString(PublishedAnimationPropertyModel state, string context)
    {
        ValidateKeys(state, context);
        PropAnimString animation = new();
        RestoreCommon(animation, animation.Keyframes, state);
        animation.DefaultValue = (string?)PublishedAnimationValueCodec.Decode(state.DefaultValue, $"{context}.default") ?? string.Empty;
        RestoreStepKeys<StringKeyframe, string?>(animation.Keyframes, state, static (key, value) => key.Value = value, context);
        if (state.IsBaked)
            animation.RestorePublishedBakedValues(RestoreReferenceSamples<string>(state, context), state.EncodedCompression);
        RestoreBakedState(animation, state);
        return animation;
    }

    private static PublishedAnimationPropertyModel CaptureObject(PropAnimObject animation, string context)
    {
        PublishedAnimationPropertyModel state = CaptureStep(animation, animation.Keyframes, AnimationKind.Object,
            animation.DefaultValue, animation.CapturePublishedBakedValues(), static key => key.Value, context);
        state.DiscreteValueRounding = animation.DiscreteValueRounding;
        return state;
    }

    private static PropAnimObject RestoreObject(PublishedAnimationPropertyModel state, string context)
    {
        ValidateKeys(state, context);
        PropAnimObject animation = new();
        RestoreCommon(animation, animation.Keyframes, state);
        animation.DefaultValue = PublishedAnimationValueCodec.Decode(state.DefaultValue, $"{context}.default");
        animation.DiscreteValueRounding = state.DiscreteValueRounding;
        RestoreStepKeys<ObjectKeyframe, object?>(animation.Keyframes, state, static (key, value) => key.Value = value, context);
        if (state.IsBaked)
            animation.RestorePublishedBakedValues(RestoreReferenceSamples<object>(state, context), state.EncodedCompression);
        RestoreBakedState(animation, state);
        return animation;
    }

    private static PublishedAnimationPropertyModel CaptureMatrix(PropAnimMatrix animation, string context)
        => CaptureStep(animation, animation.Keyframes, AnimationKind.Matrix, animation.DefaultValue,
            animation.CapturePublishedBakedValues(), static key => key.Value, context);

    private static PropAnimMatrix RestoreMatrix(PublishedAnimationPropertyModel state, string context)
    {
        ValidateKeys(state, context);
        PropAnimMatrix animation = new();
        RestoreCommon(animation, animation.Keyframes, state);
        animation.DefaultValue = Required<Matrix4x4>(state.DefaultValue, $"{context}.default");
        RestoreStepKeys<MatrixKeyframe, Matrix4x4>(animation.Keyframes, state, static (key, value) => key.Value = value, context);
        if (state.IsBaked)
            animation.RestorePublishedBakedValues(RestoreSamples<Matrix4x4>(state, context), state.EncodedCompression);
        RestoreBakedState(animation, state);
        return animation;
    }

    private static T?[] RestoreReferenceSamples<T>(PublishedAnimationPropertyModel state, string context)
        where T : class
    {
        byte[][] encoded = state.BakedValues
            ?? throw new InvalidDataException($"Published animation at '{context}' is missing baked samples.");
        if (encoded.Length != state.BakedFrameCount)
            throw new InvalidDataException($"Published animation at '{context}' has {encoded.Length} baked samples but declares {state.BakedFrameCount} frames.");
        T?[] values = new T?[encoded.Length];
        for (int i = 0; i < encoded.Length; i++)
        {
            object? value = PublishedAnimationValueCodec.Decode(encoded[i], $"{context}.baked[{i}]");
            if (value is not null && value is not T)
                throw new InvalidDataException($"Published animation at '{context}' has an invalid baked value at {i}.");
            values[i] = (T?)value;
        }
        return values;
    }
}

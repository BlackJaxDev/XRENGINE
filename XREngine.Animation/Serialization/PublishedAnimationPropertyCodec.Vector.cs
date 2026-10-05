using XREngine.Animation;

namespace XREngine;

internal static partial class PublishedAnimationPropertyCodec
{
    private static PublishedAnimationPropertyModel CaptureVector<TValue, TKeyframe>(
        PropAnimVector<TValue, TKeyframe> animation, AnimationKind kind, string context)
        where TValue : unmanaged
        where TKeyframe : VectorKeyframe<TValue>, new()
    {
        PublishedAnimationPropertyModel state = CaptureCommon(animation, animation.Keyframes, kind);
        state.DefaultValue = PublishedAnimationValueCodec.Encode(animation.DefaultValue, $"{context}.default");
        state.ConstrainKeyframedFPS = animation.ConstrainKeyframedFPS;
        state.LerpConstrainedFPS = animation.LerpConstrainedFPS;
        state.UseTangentRelativeSpeed = animation.UseTangentRelativeSpeed;
        state.BakedValues = CaptureSamples(animation.CapturePublishedBakedValues(), animation.IsBaked, context);

        foreach (TKeyframe key in animation.Keyframes)
        {
            string keyContext = $"{context}.key[{state.Keyframes.Count}]";
            state.Keyframes.Add(new PublishedAnimationKeyframeModel
            {
                Second = key.Second,
                AuthoredFrameIndex = key.AuthoredFrameIndex,
                InValue = PublishedAnimationValueCodec.Encode(key.InValue, $"{keyContext}.inValue"),
                OutValue = PublishedAnimationValueCodec.Encode(key.OutValue, $"{keyContext}.outValue"),
                InTangent = PublishedAnimationValueCodec.Encode(key.InTangent, $"{keyContext}.inTangent"),
                OutTangent = PublishedAnimationValueCodec.Encode(key.OutTangent, $"{keyContext}.outTangent"),
                InterpolationTypeIn = (int)key.InterpolationTypeIn,
                InterpolationTypeOut = (int)key.InterpolationTypeOut,
                SyncInOutValues = key.SyncInOutValues,
                SyncInOutTangentDirections = key.SyncInOutTangentDirections,
                SyncInOutTangentMagnitudes = key.SyncInOutTangentMagnitudes,
                WeightedMode = key is FloatKeyframe floatKey ? floatKey.WeightedMode : EKeyframeWeightedMode.None,
                InWeight = key is FloatKeyframe inWeightKey ? inWeightKey.InWeight : 0.0f,
                OutWeight = key is FloatKeyframe outWeightKey ? outWeightKey.OutWeight : 0.0f
            });
        }

        return state;
    }

    private static TAnimation RestoreVector<TValue, TKeyframe, TAnimation>(PublishedAnimationPropertyModel state, string context)
        where TValue : unmanaged
        where TKeyframe : VectorKeyframe<TValue>, new()
        where TAnimation : PropAnimVector<TValue, TKeyframe>, new()
    {
        ValidateKeys(state, context);
        TAnimation animation = new();
        RestoreCommon(animation, animation.Keyframes, state);
        animation.DefaultValue = Required<TValue>(state.DefaultValue, $"{context}.default");
        animation.ConstrainKeyframedFPS = state.ConstrainKeyframedFPS;
        animation.LerpConstrainedFPS = state.LerpConstrainedFPS;
        animation.UseTangentRelativeSpeed = state.UseTangentRelativeSpeed;

        for (int i = 0; i < state.Keyframes.Count; i++)
        {
            PublishedAnimationKeyframeModel source = state.Keyframes[i];
            string keyContext = $"{context}.key[{i}]";
            TKeyframe key = new()
            {
                Second = source.Second,
                AuthoredFrameIndex = source.AuthoredFrameIndex
            };
            key.RestorePublishedHandles(
                Required<TValue>(source.InValue, $"{keyContext}.inValue"),
                Required<TValue>(source.OutValue, $"{keyContext}.outValue"),
                Required<TValue>(source.InTangent, $"{keyContext}.inTangent"),
                Required<TValue>(source.OutTangent, $"{keyContext}.outTangent"),
                (EVectorInterpType)source.InterpolationTypeIn,
                (EVectorInterpType)source.InterpolationTypeOut,
                source.SyncInOutValues,
                source.SyncInOutTangentDirections,
                source.SyncInOutTangentMagnitudes);
            if (key is FloatKeyframe floatKey)
            {
                floatKey.WeightedMode = source.WeightedMode;
                floatKey.InWeight = source.InWeight;
                floatKey.OutWeight = source.OutWeight;
            }
            animation.Keyframes.Add(key);
        }

        if (state.IsBaked)
            animation.RestorePublishedBakedValues(RestoreSamples<TValue>(state, context), state.EncodedCompression);
        RestoreBakedState(animation, state);
        return animation;
    }
}

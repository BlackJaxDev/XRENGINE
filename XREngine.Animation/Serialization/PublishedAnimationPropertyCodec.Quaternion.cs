using System.Numerics;
using XREngine.Animation;

namespace XREngine;

internal static partial class PublishedAnimationPropertyCodec
{
    private static PublishedAnimationPropertyModel CaptureQuaternion(PropAnimQuaternion animation, string context)
    {
        PublishedAnimationPropertyModel state = CaptureCommon(animation, animation.Keyframes, AnimationKind.Quaternion);
        state.DefaultValue = PublishedAnimationValueCodec.Encode(animation.DefaultValue, $"{context}.default");
        state.ConstrainKeyframedFPS = animation.ConstrainKeyframedFPS;
        state.LerpConstrainedFPS = animation.LerpConstrainedFPS;
        state.BakedValues = CaptureSamples(animation.CapturePublishedBakedValues(), animation.IsBaked, context);
        foreach (QuaternionKeyframe key in animation.Keyframes)
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
                InterpolationTypeOut = (int)key.InterpolationTypeOut
            });
        }
        return state;
    }

    private static PropAnimQuaternion RestoreQuaternion(PublishedAnimationPropertyModel state, string context)
    {
        ValidateKeys(state, context);
        PropAnimQuaternion animation = new();
        RestoreCommon(animation, animation.Keyframes, state);
        animation.DefaultValue = Required<Quaternion>(state.DefaultValue, $"{context}.default");
        animation.ConstrainKeyframedFPS = state.ConstrainKeyframedFPS;
        animation.LerpConstrainedFPS = state.LerpConstrainedFPS;
        for (int i = 0; i < state.Keyframes.Count; i++)
        {
            PublishedAnimationKeyframeModel source = state.Keyframes[i];
            string keyContext = $"{context}.key[{i}]";
            QuaternionKeyframe key = new()
            {
                Second = source.Second,
                AuthoredFrameIndex = source.AuthoredFrameIndex,
                InValue = Required<Quaternion>(source.InValue, $"{keyContext}.inValue"),
                OutValue = Required<Quaternion>(source.OutValue, $"{keyContext}.outValue"),
                InTangent = Required<Quaternion>(source.InTangent, $"{keyContext}.inTangent"),
                OutTangent = Required<Quaternion>(source.OutTangent, $"{keyContext}.outTangent"),
                InterpolationTypeIn = (ERadialInterpType)source.InterpolationTypeIn,
                InterpolationTypeOut = (ERadialInterpType)source.InterpolationTypeOut
            };
            animation.Keyframes.Add(key);
        }
        if (state.IsBaked)
            animation.RestorePublishedBakedValues(RestoreSamples<Quaternion>(state, context), state.EncodedCompression);
        RestoreBakedState(animation, state);
        return animation;
    }
}

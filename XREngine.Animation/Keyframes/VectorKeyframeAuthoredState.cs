namespace XREngine.Animation;

/// <summary>The complete editable handle state of one vector keyframe.</summary>
internal readonly record struct VectorKeyframeAuthoredState<T>(
    T InValue, T OutValue, T InTangent, T OutTangent,
    EVectorInterpType InterpolationIn, EVectorInterpType InterpolationOut,
    bool SyncValues, bool SyncDirections, bool SyncMagnitudes,
    EKeyframeWeightedMode WeightedMode, float InWeight, float OutWeight)
    where T : unmanaged;

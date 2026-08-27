namespace XREngine.Animation;

/// <summary>
/// Selects which tangent handles contribute authored time weights to a scalar
/// keyframe segment. Values intentionally match Unity's <c>WeightedMode</c>.
/// </summary>
[Flags]
public enum EKeyframeWeightedMode
{
    None = 0,
    In = 1,
    Out = 2,
    Both = In | Out,
}

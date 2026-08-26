namespace XREngine.Components;

/// <summary>
/// Lower-runtime scalar curve used to distribute a chain coefficient along its
/// particles. Animation assemblies may adapt richer authoring curves to this
/// value contract without becoming a Runtime.Core dependency.
/// </summary>
public sealed class PhysicsChainCoefficientCurve
{
    private readonly List<PhysicsChainCoefficientKeyframe> _keyframes = [];

    public IList<PhysicsChainCoefficientKeyframe> Keyframes => _keyframes;

    public float Evaluate(float position)
    {
        if (_keyframes.Count == 0)
            return 1.0f;
        if (position <= _keyframes[0].Second)
            return _keyframes[0].OutValue;

        for (int i = 1; i < _keyframes.Count; ++i)
        {
            PhysicsChainCoefficientKeyframe previous = _keyframes[i - 1];
            PhysicsChainCoefficientKeyframe next = _keyframes[i];
            if (position <= next.Second)
            {
                float range = next.Second - previous.Second;
                if (range <= 1e-6f)
                    return next.OutValue;

                float t = Math.Clamp((position - previous.Second) / range, 0.0f, 1.0f);
                float t2 = t * t;
                float t3 = t2 * t;
                float h00 = (2.0f * t3) - (3.0f * t2) + 1.0f;
                float h10 = t3 - (2.0f * t2) + t;
                float h01 = (-2.0f * t3) + (3.0f * t2);
                float h11 = t3 - t2;
                return (h00 * previous.OutValue)
                    + (h10 * previous.OutTangent * range)
                    + (h01 * next.InValue)
                    + (h11 * next.InTangent * range);
            }
        }

        return _keyframes[^1].OutValue;
    }
}

/// <summary>
/// Animation-independent Hermite key used by the lower physics runtime.
/// </summary>
public readonly record struct PhysicsChainCoefficientKeyframe(
    float Second,
    float InValue,
    float OutValue,
    float InTangent,
    float OutTangent)
{
    public PhysicsChainCoefficientKeyframe(float second, float value, float inTangent, float outTangent)
        : this(second, value, value, inTangent, outTangent)
    {
    }
}

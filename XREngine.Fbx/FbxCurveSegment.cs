namespace XREngine.Fbx;

/// <summary>
/// Interpolation data of one FBX animation curve segment. Entry i of
/// <see cref="FbxScalarCurve.Segments"/> describes the segment from key i to key i + 1.
/// </summary>
/// <param name="Interpolation">The interpolation of the segment.</param>
/// <param name="RightSlope">For cubic segments: the slope that leaves key i, in value units per second.</param>
/// <param name="NextLeftSlope">For cubic segments: the slope that arrives at key i + 1, in value units per second.</param>
/// <param name="RightWeight">For cubic segments: the tangent length at key i, as a fraction of the segment duration.</param>
/// <param name="NextLeftWeight">For cubic segments: the tangent length at key i + 1, as a fraction of the segment duration.</param>
public readonly record struct FbxCurveSegment(
    EFbxCurveInterpolation Interpolation,
    float RightSlope = 0.0f,
    float NextLeftSlope = 0.0f,
    float RightWeight = FbxCurveSegment.DefaultWeight,
    float NextLeftWeight = FbxCurveSegment.DefaultWeight)
{
    /// <summary>The tangent weight of an unweighted FBX tangent (one third of the segment duration).</summary>
    public const float DefaultWeight = 1.0f / 3.0f;

    /// <summary>A linear segment.</summary>
    public static FbxCurveSegment Linear => new(EFbxCurveInterpolation.Linear);
}

namespace XREngine.Fbx;

public static class FbxAnimationTime
{
    public const long TicksPerSecond = 46186158000L;

    public static float ToSeconds(long ticks)
        => (float)(ticks / (double)TicksPerSecond);
}

/// <summary>
/// One FBX animation curve: key times in FBX ticks, key values, and the interpolation of each
/// segment between two keys. Without <paramref name="Segments"/>, all segments are linear.
/// Outside the key range, the curve holds its first or last key value.
/// </summary>
/// <param name="KeyTimes">Strictly increasing key times in FBX ticks.</param>
/// <param name="Values">One value for each key time.</param>
/// <param name="Segments">Optional interpolation data, one entry for each key. Entry i describes the segment from key i to key i + 1.</param>
public sealed record FbxScalarCurve(
    IReadOnlyList<long> KeyTimes,
    IReadOnlyList<float> Values,
    IReadOnlyList<FbxCurveSegment>? Segments = null)
{
    public bool HasKeys => KeyTimes.Count > 0 && Values.Count > 0;

    /// <summary>True when at least one segment is not linear, so straight lines between the keys do not give the curve.</summary>
    public bool HasNonLinearSegments
    {
        get
        {
            if (Segments is null)
                return false;
            int segmentCount = Math.Min(Segments.Count, KeyCount - 1);
            for (int index = 0; index < segmentCount; index++)
                if (Segments[index].Interpolation != EFbxCurveInterpolation.Linear)
                    return true;
            return false;
        }
    }

    private int KeyCount => Math.Min(KeyTimes.Count, Values.Count);

    /// <summary>
    /// Returns the curve value at <paramref name="keyTime"/> (FBX ticks). The method finds the
    /// segment with a binary search and applies the interpolation of that segment.
    /// </summary>
    public float Evaluate(long keyTime)
    {
        if (!HasKeys)
            return 0.0f;
        if (keyTime <= KeyTimes[0])
            return Values[0];

        int lastIndex = KeyCount - 1;
        if (keyTime >= KeyTimes[lastIndex])
            return Values[lastIndex];

        // Find the last key at or before keyTime. The checks above make 0 <= low < lastIndex.
        int low = 0;
        int high = lastIndex;
        while (high - low > 1)
        {
            int middle = low + ((high - low) >> 1);
            if (KeyTimes[middle] <= keyTime)
                low = middle;
            else
                high = middle;
        }

        long previousTime = KeyTimes[low];
        long nextTime = KeyTimes[low + 1];
        float previousValue = Values[low];
        float nextValue = Values[low + 1];
        if (keyTime == previousTime)
            return previousValue;

        FbxCurveSegment segment = Segments is not null && low < Segments.Count ? Segments[low] : FbxCurveSegment.Linear;
        double fraction = (keyTime - previousTime) / (double)(nextTime - previousTime);
        return segment.Interpolation switch
        {
            EFbxCurveInterpolation.Constant => previousValue,
            EFbxCurveInterpolation.ConstantNext => nextValue,
            EFbxCurveInterpolation.Cubic => EvaluateCubic(segment, previousValue, nextValue, (nextTime - previousTime) / (double)FbxAnimationTime.TicksPerSecond, fraction),
            _ => (float)(previousValue + ((nextValue - previousValue) * fraction)),
        };
    }

    /// <summary>
    /// Returns the step times of constant segments. A step changes the value at one instant, so
    /// a sampler that connects samples with straight lines needs a sample just before the step
    /// and a sample at the step. <c>Before</c> is <paramref name="offsetTicks"/> before the value
    /// change (at most half the segment), and <c>At</c> is the first time with the new value.
    /// </summary>
    public IEnumerable<(long Before, long At)> EnumerateSteps(long offsetTicks)
    {
        if (Segments is null)
            yield break;

        int segmentCount = Math.Min(Segments.Count, KeyCount - 1);
        for (int index = 0; index < segmentCount; index++)
        {
            long start = KeyTimes[index];
            long end = KeyTimes[index + 1];
            long offset = Math.Max(1L, Math.Min(offsetTicks, (end - start) / 2));
            switch (Segments[index].Interpolation)
            {
                case EFbxCurveInterpolation.Constant when Values[index] != Values[index + 1]:
                    yield return (end - offset, end);
                    break;
                case EFbxCurveInterpolation.ConstantNext when Values[index] != Values[index + 1]:
                    yield return (start, start + offset);
                    break;
            }
        }
    }

    /// <summary>
    /// Evaluates a weighted cubic segment. The segment is a Bezier curve in (time, value) with
    /// the control points (0, v0), (w0, v0 + s0 w0 d), (1 - w1, v1 - s1 w1 d), (1, v1), where
    /// time is the segment fraction, d is the segment duration in seconds, s0 and s1 are the
    /// slopes, and w0 and w1 are the tangent weights. The method finds the Bezier parameter
    /// whose time equals <paramref name="fraction"/> and returns the value at that parameter.
    /// </summary>
    private static float EvaluateCubic(FbxCurveSegment segment, float v0, float v1, double durationSeconds, double fraction)
    {
        double w0 = Math.Clamp((double)segment.RightWeight, 0.0, 1.0);
        double w1 = Math.Clamp((double)segment.NextLeftWeight, 0.0, 1.0);
        double y0 = v0;
        double y1 = v0 + (segment.RightSlope * w0 * durationSeconds);
        double y2 = v1 - (segment.NextLeftSlope * w1 * durationSeconds);
        double y3 = v1;
        double u = SolveBezierParameter(w0, 1.0 - w1, fraction);
        double inverse = 1.0 - u;
        return (float)((inverse * inverse * inverse * y0)
            + (3.0 * inverse * inverse * u * y1)
            + (3.0 * inverse * u * u * y2)
            + (u * u * u * y3));
    }

    /// <summary>
    /// Solves x(u) = <paramref name="x"/> for the Bezier time polynomial with the control
    /// points 0, <paramref name="x1"/>, <paramref name="x2"/>, 1. With x1 and x2 in [0, 1]
    /// the polynomial does not decrease, so Newton steps that stay inside a bisection bracket
    /// converge. With the default weights (1/3 and 2/3) x(u) = u.
    /// </summary>
    private static double SolveBezierParameter(double x1, double x2, double x)
    {
        double low = 0.0;
        double high = 1.0;
        double u = x;
        for (int iteration = 0; iteration < 32; iteration++)
        {
            double inverse = 1.0 - u;
            double value = (3.0 * inverse * inverse * u * x1) + (3.0 * inverse * u * u * x2) + (u * u * u) - x;
            if (Math.Abs(value) < 1e-12)
                return u;
            if (value < 0.0)
                low = u;
            else
                high = u;
            double derivative = (3.0 * inverse * inverse * x1) + (6.0 * inverse * u * (x2 - x1)) + (3.0 * u * u * (1.0 - x2));
            double next = derivative > 1e-12 ? u - (value / derivative) : 0.5 * (low + high);
            u = next > low && next < high ? next : 0.5 * (low + high);
        }
        return u;
    }
}

public sealed record FbxNodeAnimationBinding(
    long ModelObjectId,
    FbxScalarCurve? TranslationX,
    FbxScalarCurve? TranslationY,
    FbxScalarCurve? TranslationZ,
    FbxScalarCurve? RotationX,
    FbxScalarCurve? RotationY,
    FbxScalarCurve? RotationZ,
    FbxScalarCurve? ScaleX,
    FbxScalarCurve? ScaleY,
    FbxScalarCurve? ScaleZ)
{
    public IEnumerable<long> EnumerateKeyTimes()
    {
        foreach (FbxScalarCurve? curve in GetCurves())
        {
            if (curve is null)
                continue;

            foreach (long keyTime in curve.KeyTimes)
                yield return keyTime;
        }
    }

    public IEnumerable<FbxScalarCurve?> GetCurves()
    {
        yield return TranslationX;
        yield return TranslationY;
        yield return TranslationZ;
        yield return RotationX;
        yield return RotationY;
        yield return RotationZ;
        yield return ScaleX;
        yield return ScaleY;
        yield return ScaleZ;
    }
}

public sealed record FbxBlendShapeAnimationBinding(
    long GeometryObjectId,
    long ChannelObjectId,
    string BlendShapeName,
    float FullWeight,
    float DefaultDeformPercent,
    FbxScalarCurve WeightCurve)
{
    public IEnumerable<long> EnumerateKeyTimes()
        => WeightCurve.KeyTimes;
}

public sealed record FbxAnimationStackBinding(
    long StackObjectId,
    string Name,
    int ObjectIndex,
    IReadOnlyList<FbxNodeAnimationBinding> NodeAnimations,
    IReadOnlyList<FbxBlendShapeAnimationBinding> BlendShapeAnimations)
{
    public IEnumerable<long> EnumerateKeyTimes()
    {
        foreach (FbxNodeAnimationBinding animation in NodeAnimations)
            foreach (long keyTime in animation.EnumerateKeyTimes())
                yield return keyTime;

        foreach (FbxBlendShapeAnimationBinding animation in BlendShapeAnimations)
            foreach (long keyTime in animation.EnumerateKeyTimes())
                yield return keyTime;
    }
}

public sealed class FbxAnimationDocument
{
    private readonly Dictionary<long, FbxScalarCurve> _curvesByObjectId;

    internal FbxAnimationDocument(FbxAnimationStackBinding[] stacks, Dictionary<long, FbxScalarCurve> curvesByObjectId)
    {
        Stacks = stacks;
        _curvesByObjectId = curvesByObjectId;
    }

    public IReadOnlyList<FbxAnimationStackBinding> Stacks { get; }

    public IReadOnlyDictionary<long, FbxScalarCurve> CurvesByObjectId => _curvesByObjectId;

    public bool TryGetCurve(long curveObjectId, out FbxScalarCurve curve)
        => _curvesByObjectId.TryGetValue(curveObjectId, out curve!);
}
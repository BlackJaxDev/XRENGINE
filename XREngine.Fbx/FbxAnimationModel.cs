namespace XREngine.Fbx;

public static class FbxAnimationTime
{
    public const long TicksPerSecond = 46186158000L;

    public static float ToSeconds(long ticks)
        => (float)(ticks / (double)TicksPerSecond);
}

public sealed record FbxScalarCurve(
    IReadOnlyList<long> KeyTimes,
    IReadOnlyList<float> Values)
{
    public bool HasKeys => KeyTimes.Count > 0 && Values.Count > 0;

    public float Evaluate(long keyTime)
    {
        if (!HasKeys)
            return 0.0f;
        if (keyTime <= KeyTimes[0])
            return Values[0];

        int lastIndex = Math.Min(KeyTimes.Count, Values.Count) - 1;
        if (keyTime >= KeyTimes[lastIndex])
            return Values[lastIndex];

        for (int index = 1; index <= lastIndex; index++)
        {
            long nextTime = KeyTimes[index];
            if (keyTime > nextTime)
                continue;

            long previousTime = KeyTimes[index - 1];
            float previousValue = Values[index - 1];
            float nextValue = Values[index];
            if (nextTime == previousTime)
                return nextValue;

            float t = (float)((keyTime - previousTime) / (double)(nextTime - previousTime));
            return previousValue + ((nextValue - previousValue) * t);
        }

        return Values[lastIndex];
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
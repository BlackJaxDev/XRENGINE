using System.Diagnostics;
using System.Numerics;
using XREngine.Animation;

namespace XREngine.Browser;

/// <summary>Cooked adapter to the engine's authored cadence and baked linear/spherical interpolation policy.</summary>
internal sealed class BrowserCookedAnimationClip
{
    private readonly TransformState[] _frames;
    private readonly float[] _morphWeights;
    private readonly AuthoredCadence _cadence;
    private readonly bool _endInclusiveSamples;

    internal BrowserCookedAnimationClip(BrowserCookedAnimationClipDto dto, int boneCount, int morphCount)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(dto.Frames);
        ArgumentNullException.ThrowIfNull(dto.MorphWeights);
        ValidateName(dto.Name);
        if (dto.FramesPerSecond is < 1 or > 120 || dto.FrameCount is < 2 or > 600 ||
            dto.Frames.Length != checked(dto.FrameCount * boneCount * 10) ||
            (dto.MorphWeights.Length != 0 && dto.MorphWeights.Length != dto.FrameCount * morphCount))
            throw new ArgumentException("Cooked clip sample dimensions exceed the admitted fixed-rate pose profile.");
        Name = dto.Name;
        BoneCount = boneCount;
        MorphCount = morphCount;
        FrameCount = dto.FrameCount;
        Loop = dto.Loop;
        _endInclusiveSamples = dto.EndInclusiveSamples;
        _cadence = new AuthoredCadence(dto.FrameCount - (dto.EndInclusiveSamples ? 1 : 0), dto.FramesPerSecond);
        LengthTicks = _cadence.GetLengthStopwatchTicks(Stopwatch.Frequency);
        _frames = new TransformState[dto.FrameCount * boneCount];
        for (int i = 0; i < _frames.Length; i++) _frames[i] = DecodePose(dto.Frames.AsSpan(i * 10, 10));
        _morphWeights = (float[])dto.MorphWeights.Clone();
        foreach (float weight in _morphWeights)
            if (!float.IsFinite(weight) || MathF.Abs(weight) > 100) throw new ArgumentException("Cooked morph weights must be bounded and finite.");
    }

    public string Name { get; }
    public int BoneCount { get; }
    public int MorphCount { get; }
    public int FrameCount { get; }
    public bool Loop { get; }
    public long LengthTicks { get; }
    public long RetainedBytes => (long)_frames.Length * 44 + (long)_morphWeights.Length * 4;

    internal void Sample(long ticks, Span<TransformState> pose, Span<Vector2> morphs)
    {
        if (pose.Length != BoneCount || morphs.Length != MorphCount) throw new ArgumentException("Pose destination shape mismatch.");
        ticks = Loop ? ticks % LengthTicks : Math.Clamp(ticks, 0, LengthTicks);
        if (ticks < 0) ticks += LengthTicks;
        int first = _endInclusiveSamples && !Loop && ticks == LengthTicks
            ? FrameCount - 1 : _cadence.GetFrameFloor(ticks, Stopwatch.Frequency);
        int next = first == FrameCount - 1 ? (Loop ? 0 : first) : first + 1;
        float fraction = _cadence.GetFrameFraction(ticks, Stopwatch.Frequency);
        for (int bone = 0; bone < BoneCount; bone++)
        {
            TransformState a = _frames[first * BoneCount + bone], b = _frames[next * BoneCount + bone];
            pose[bone] = new TransformState
            {
                Translation = Vector3.Lerp(a.Translation, b.Translation, fraction),
                Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, fraction),
                Scale = Vector3.Lerp(a.Scale, b.Scale, fraction),
                Order = ETransformOrder.TRS
            };
        }
        for (int shape = 0; shape < MorphCount; shape++)
        {
            float weight = 0;
            if (_morphWeights.Length != 0)
            {
                float a = _morphWeights[first * MorphCount + shape], b = _morphWeights[next * MorphCount + shape];
                weight = a + (b - a) * fraction;
            }
            morphs[shape] = new Vector2(shape, weight);
        }
    }

    internal static TransformState DecodePose(ReadOnlySpan<float> values)
    {
        foreach (float value in values) if (!float.IsFinite(value)) throw new ArgumentException("Cooked local poses must be finite.");
        Vector3 translation = new(values[0], values[1], values[2]);
        Quaternion rotation = new(values[3], values[4], values[5], values[6]);
        Vector3 scale = new(values[7], values[8], values[9]);
        if (translation.LengthSquared() > 100_000_000 || MathF.Abs(rotation.LengthSquared() - 1) > 0.001f ||
            scale.X is < 0.001f or > 100 || scale.Y is < 0.001f or > 100 || scale.Z is < 0.001f or > 100)
            throw new ArgumentException("Cooked TRS requires bounded translation, a unit quaternion and positive nonsingular scale.");
        return new TransformState { Translation = translation, Rotation = Quaternion.Normalize(rotation), Scale = scale, Order = ETransformOrder.TRS };
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) throw new ArgumentException("Clip names require one to 64 ASCII identifier characters.");
        foreach (char value in name)
            if (!(char.IsAsciiLetterOrDigit(value) || value is '_' or '-' or '.')) throw new ArgumentException("Clip names contain an unsupported character.");
    }
}

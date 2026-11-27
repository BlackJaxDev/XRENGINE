using System.Numerics;

namespace XREngine.Animation;

/// <summary>
/// Allocation-free circular history buffer that records evaluated humanoid poses on the
/// animation thread and resamples the four most recent 30 Hz context frames whenever a
/// <see cref="MotionBricksLocomotionMotion"/> state becomes active.
/// </summary>
public sealed class HumanoidPoseHistoryBuffer
{
    public const int ContextFrameCount = 4;
    public const float ContextFrameStepSeconds = 1.0f / 30.0f;
    private const int RingCapacity = 16;

    private readonly HumanoidPoseHistoryFrame[] _ring = new HumanoidPoseHistoryFrame[RingCapacity];
    private int _writeIndex;
    private int _count;
    private float _clockSeconds;

    public HumanoidPoseHistoryBuffer()
    {
        for (int i = 0; i < RingCapacity; i++)
            _ring[i] = new HumanoidPoseHistoryFrame();
    }

    public int SampleCount => _count;

    public void Clear()
    {
        _writeIndex = 0;
        _count = 0;
        _clockSeconds = 0.0f;
        for (int i = 0; i < RingCapacity; i++)
            _ring[i].Clear();
    }

    /// <summary>
    /// Records one evaluated humanoid frame into the circular buffer without heap allocation.
    /// </summary>
    public void RecordFrame(
        Vector3 bodyPosition,
        Quaternion bodyRotation,
        ReadOnlySpan<float> muscleValues,
        float deltaSeconds)
    {
        float dt = float.IsFinite(deltaSeconds) && deltaSeconds > 0.0f ? deltaSeconds : 0.0f;
        if (_count > 0)
            _clockSeconds += dt;

        HumanoidPoseHistoryFrame slot = _ring[_writeIndex];
        slot.BodyPosition = IsFinite(bodyPosition) ? bodyPosition : Vector3.Zero;
        slot.BodyRotation = NormalizeOrIdentity(bodyRotation);
        Array.Clear(slot.MuscleValues);
        int copyCount = Math.Min(muscleValues.Length, slot.MuscleValues.Length);
        muscleValues.Slice(0, copyCount).CopyTo(slot.MuscleValues);
        slot.TimeSeconds = _clockSeconds;
        slot.HasValidPose = true;

        _writeIndex = (_writeIndex + 1) % RingCapacity;
        if (_count < RingCapacity)
            _count++;
    }

    /// <summary>
    /// Samples four 30 Hz context frames (oldest to newest) into
    /// <paramref name="destinationFourFrames"/> using linear muscle interpolation and
    /// quaternion slerp. Returns false when no pose history exists yet.
    /// </summary>
    public bool TrySampleFourContextFrames(HumanoidPoseHistoryFrame[] destinationFourFrames)
    {
        ArgumentNullException.ThrowIfNull(destinationFourFrames);
        if (destinationFourFrames.Length < ContextFrameCount)
            throw new ArgumentException($"Destination must hold at least {ContextFrameCount} frames.", nameof(destinationFourFrames));

        if (_count == 0)
            return false;

        float newestTime = _clockSeconds;
        for (int i = 0; i < ContextFrameCount; i++)
        {
            float targetTime = newestTime - (ContextFrameCount - 1 - i) * ContextFrameStepSeconds;
            SampleAtTime(targetTime, destinationFourFrames[i]);
        }

        return true;
    }

    private void SampleAtTime(float targetTime, HumanoidPoseHistoryFrame destination)
    {
        int oldestIndex = _count < RingCapacity ? 0 : _writeIndex;
        int newestIndex = (_writeIndex - 1 + RingCapacity) % RingCapacity;

        HumanoidPoseHistoryFrame oldest = _ring[oldestIndex];
        if (targetTime <= oldest.TimeSeconds || _count == 1)
        {
            destination.CopyFrom(oldest);
            return;
        }

        HumanoidPoseHistoryFrame newest = _ring[newestIndex];
        if (targetTime >= newest.TimeSeconds)
        {
            destination.CopyFrom(newest);
            return;
        }

        int prevIdx = oldestIndex;
        for (int step = 1; step < _count; step++)
        {
            int curIdx = (oldestIndex + step) % RingCapacity;
            HumanoidPoseHistoryFrame cur = _ring[curIdx];
            if (cur.TimeSeconds >= targetTime)
            {
                HumanoidPoseHistoryFrame prev = _ring[prevIdx];
                float span = cur.TimeSeconds - prev.TimeSeconds;
                float t = span > 1.0e-6f ? Math.Clamp((targetTime - prev.TimeSeconds) / span, 0.0f, 1.0f) : 1.0f;
                InterpolateFrames(prev, cur, t, targetTime, destination);
                return;
            }

            prevIdx = curIdx;
        }

        destination.CopyFrom(newest);
    }

    private static void InterpolateFrames(
        HumanoidPoseHistoryFrame a,
        HumanoidPoseHistoryFrame b,
        float t,
        float targetTime,
        HumanoidPoseHistoryFrame destination)
    {
        destination.BodyPosition = Vector3.Lerp(a.BodyPosition, b.BodyPosition, t);
        destination.BodyRotation = NormalizeOrIdentity(Quaternion.Slerp(a.BodyRotation, b.BodyRotation, t));
        float[] dst = destination.MuscleValues;
        float[] srcA = a.MuscleValues;
        float[] srcB = b.MuscleValues;
        for (int i = 0; i < dst.Length; i++)
            dst[i] = srcA[i] + (srcB[i] - srcA[i]) * t;

        destination.TimeSeconds = targetTime;
        destination.HasValidPose = true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Quaternion NormalizeOrIdentity(Quaternion q)
    {
        float lenSq = q.LengthSquared();
        if (!float.IsFinite(lenSq) || lenSq <= 1.0e-12f)
            return Quaternion.Identity;

        return Quaternion.Normalize(q);
    }
}

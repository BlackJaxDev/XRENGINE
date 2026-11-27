using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Preallocated circular pose recorder that captures played SOMA frames at arbitrary
/// engine frame rates and resamples 4 evenly spaced 30 Hz context poses ending at the
/// most recent pose without heap allocations.
/// </summary>
public sealed class SomaContextHistoryBuffer
{
    // Retain at least 0.5 s at 500 FPS (and > 0.25 s at 1000 FPS) so the 100 ms 30 Hz SOMA
    // context window never clamps older frames to frame 0 on high-framerate Vulkan builds.
    private const int Capacity = 256;
    private const int JointCount = SomaModelContract.JointCount;
    private const float ContextStepSeconds = 1.0f / SomaModelContract.SampleRateHz;

    private readonly SomaPoseTransform[] _frames = new SomaPoseTransform[Capacity * JointCount];
    private readonly double[] _timestamps = new double[Capacity];

    private int _writeIndex;
    private int _count;
    private double _currentTimeSeconds;

    public int RecordedFrameCount => _count;

    public void Clear()
    {
        _writeIndex = 0;
        _count = 0;
        _currentTimeSeconds = 0.0;
    }

    /// <summary>
    /// Seeds all 4 initial 30 Hz context slots from a single pose (for example, neutral rest
    /// pose on cold start or a stationary standing pose).
    /// </summary>
    public void SeedFromPose(ReadOnlySpan<SomaPoseTransform> pose)
    {
        if (pose.Length < JointCount)
            throw new ArgumentException($"Pose must contain {JointCount} joints.", nameof(pose));

        Clear();
        for (int i = 0; i < SomaModelContract.ContextPoseCount; i++)
            RecordPose(pose, i == 0 ? 0.0f : ContextStepSeconds);
    }

    /// <summary>
    /// Seeds the history buffer from 4 consecutive 30 Hz context poses (oldest at slot 0,
    /// newest at slot 3).
    /// </summary>
    public void SeedFromFourPoses(ReadOnlySpan<SomaPoseTransform> fourPoses)
    {
        if (fourPoses.Length < SomaModelContract.ContextPoseCount * JointCount)
            throw new ArgumentException("Four poses (4 x 30 joints) are required.", nameof(fourPoses));

        Clear();
        for (int i = 0; i < SomaModelContract.ContextPoseCount; i++)
        {
            ReadOnlySpan<SomaPoseTransform> slice = fourPoses.Slice(i * JointCount, JointCount);
            RecordPose(slice, i == 0 ? 0.0f : ContextStepSeconds);
        }
    }

    /// <summary>
    /// Appends one played SOMA pose advanced by <paramref name="deltaSeconds"/>.
    /// </summary>
    public void RecordPose(ReadOnlySpan<SomaPoseTransform> pose, float deltaSeconds)
    {
        if (pose.Length < JointCount)
            throw new ArgumentException($"Pose must contain {JointCount} joints.", nameof(pose));

        if (float.IsFinite(deltaSeconds) && deltaSeconds > 0.0f)
            _currentTimeSeconds += deltaSeconds;

        int slot = _writeIndex;
        pose.Slice(0, JointCount).CopyTo(_frames.AsSpan(slot * JointCount, JointCount));
        _timestamps[slot] = _currentTimeSeconds;

        _writeIndex = (_writeIndex + 1) % Capacity;
        if (_count < Capacity)
            _count++;
    }

    /// <summary>
    /// Applies a world-space root offset (for example after Jolt physics collision clamping)
    /// across all recorded frames so upcoming replans stay anchored to the physical capsule.
    /// </summary>
    public void ApplyRootTranslationOffset(Vector3 offset)
    {
        if (_count == 0 || offset == Vector3.Zero)
            return;

        for (int i = 0; i < _count; i++)
        {
            int slot = GetLogicalSlotIndex(i);
            int rootIndex = slot * JointCount;
            SomaPoseTransform root = _frames[rootIndex];
            _frames[rootIndex] = new SomaPoseTransform(root.Position + offset, root.Rotation);
        }
    }

    /// <summary>
    /// Resamples 4 evenly spaced 30 Hz context poses ending at the latest recorded frame
    /// into <paramref name="destination"/> (layout <c>[4 * 30]</c>).
    /// </summary>
    public bool TrySampleContextToken(Span<SomaPoseTransform> destination)
    {
        if (_count == 0)
            return false;

        if (destination.Length < SomaModelContract.ContextPoseCount * JointCount)
            throw new ArgumentException("Destination span must hold 4 poses x 30 joints.", nameof(destination));

        int newestSlot = GetLogicalSlotIndex(_count - 1);
        double newestTime = _timestamps[newestSlot];

        for (int c = 0; c < SomaModelContract.ContextPoseCount; c++)
        {
            double sampleTime = newestTime - (SomaModelContract.ContextPoseCount - 1 - c) * ContextStepSeconds;
            SampleAtTime(sampleTime, destination.Slice(c * JointCount, JointCount));
        }

        return true;
    }

    private void SampleAtTime(double targetTime, Span<SomaPoseTransform> destination)
    {
        int oldestSlot = GetLogicalSlotIndex(0);
        if (_count == 1 || targetTime <= _timestamps[oldestSlot])
        {
            _frames.AsSpan(oldestSlot * JointCount, JointCount).CopyTo(destination);
            return;
        }

        int newestSlot = GetLogicalSlotIndex(_count - 1);
        if (targetTime >= _timestamps[newestSlot])
        {
            _frames.AsSpan(newestSlot * JointCount, JointCount).CopyTo(destination);
            return;
        }

        // Walk backward from newest (4 frames at 30 Hz spans only ~100 ms).
        int upperLogical = _count - 1;
        while (upperLogical > 1 && _timestamps[GetLogicalSlotIndex(upperLogical - 1)] > targetTime)
            upperLogical--;

        int lowerSlot = GetLogicalSlotIndex(upperLogical - 1);
        int upperSlot = GetLogicalSlotIndex(upperLogical);
        double t0 = _timestamps[lowerSlot];
        double t1 = _timestamps[upperSlot];
        double span = t1 - t0;
        float blend = span > 1.0e-7 ? (float)Math.Clamp((targetTime - t0) / span, 0.0, 1.0) : 0.0f;

        int base0 = lowerSlot * JointCount;
        int base1 = upperSlot * JointCount;
        for (int joint = 0; joint < JointCount; joint++)
            destination[joint] = SomaPoseTransform.Lerp(_frames[base0 + joint], _frames[base1 + joint], blend);
    }

    private int GetLogicalSlotIndex(int logicalIndex)
    {
        int oldestSlot = _count < Capacity ? 0 : _writeIndex;
        return (oldestSlot + logicalIndex) % Capacity;
    }
}

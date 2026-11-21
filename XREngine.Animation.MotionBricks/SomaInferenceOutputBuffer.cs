namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Preallocated host output tensor buffers and decoded 30 Hz SOMA motion storage.
/// Reuse one instance per inference session to keep decoding free of heap allocations.
/// </summary>
public sealed class SomaInferenceOutputBuffer
{
    public float[] PredGlobalMotions { get; } = new float[SomaModelContract.PredGlobalMotionsTensorElementCount];
    public int[] PredNumTokens { get; } = new int[SomaModelContract.PredNumTokensTensorElementCount];

    /// <summary>
    /// Decoded local joint transforms laid out as <c>[frame * 30 + joint]</c> for up to 58 valid frames.
    /// Joint 0 (<c>Hips</c>) is in runtime world space; joints 1..29 are parent-relative local transforms.
    /// </summary>
    public SomaPoseTransform[] DecodedLocalPoses { get; } =
        new SomaPoseTransform[SomaModelContract.MaxValidFrameCount * SomaModelContract.JointCount];

    /// <summary>
    /// Decoded foot-contact weights clamped to <c>[0, 1]</c>, laid out as <c>[frame * 4 + channel]</c>
    /// for <c>LeftFoot</c>, <c>LeftToeBase</c>, <c>RightFoot</c>, and <c>RightToeBase</c>.
    /// </summary>
    public float[] FootContacts { get; } =
        new float[SomaModelContract.MaxValidFrameCount * SomaModelContract.FootContactCount];

    /// <summary>
    /// Scratch storage for endpoint correction start and end deltas across the 30 joints.
    /// </summary>
    internal SomaPoseTransform[] StartEndpointDeltas { get; } = new SomaPoseTransform[SomaModelContract.JointCount];
    internal SomaPoseTransform[] EndEndpointDeltas { get; } = new SomaPoseTransform[SomaModelContract.JointCount];

    /// <summary>
    /// Number of valid decoded frames in <see cref="DecodedLocalPoses"/> and <see cref="FootContacts"/>.
    /// </summary>
    public int ValidFrameCount { get; internal set; }

    public ReadOnlySpan<SomaPoseTransform> GetFrameLocalPose(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)ValidFrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        return DecodedLocalPoses.AsSpan(frameIndex * SomaModelContract.JointCount, SomaModelContract.JointCount);
    }

    public ReadOnlySpan<float> GetFrameFootContacts(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)ValidFrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        return FootContacts.AsSpan(frameIndex * SomaModelContract.FootContactCount, SomaModelContract.FootContactCount);
    }

    public void Clear()
    {
        Array.Clear(PredGlobalMotions);
        Array.Clear(PredNumTokens);
        Array.Clear(DecodedLocalPoses);
        Array.Clear(FootContacts);
        Array.Clear(StartEndpointDeltas);
        Array.Clear(EndEndpointDeltas);
        ValidFrameCount = 0;
    }
}

using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Stores a 30 Hz SOMA 30-joint mocap animation clip normalized so frame 0 begins at ground XZ
/// origin <c>(0, 0)</c> facing canonical <c>+Z</c> (<c>yaw = 0</c>), supporting zero-allocation
/// placed playback and 4-frame transition target extraction.
/// </summary>
public sealed class SomaMotionClip
{
    private const int JointCount = SomaModelContract.JointCount;
    private const float LoopCrossfadeSeconds = 0.25f;

    public string Name { get; }
    public int FrameCount { get; }
    public float SampleRateHz { get; }
    public float DurationSeconds { get; }

    /// <summary>
    /// Flat array of <c>FrameCount * 30</c> local joint transforms (joint 0 in root space,
    /// joints 1..29 parent-relative local transforms).
    /// </summary>
    public SomaPoseTransform[] Poses { get; }

    public SomaMotionClip(string name, SomaPoseTransform[] poses, float sampleRateHz = SomaModelContract.SampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(poses);
        if (poses.Length < JointCount || poses.Length % JointCount != 0)
            throw new ArgumentException($"Poses length must be a non-zero multiple of {JointCount}.", nameof(poses));

        Name = string.IsNullOrWhiteSpace(name) ? "UnnamedClip" : name;
        Poses = poses;
        FrameCount = poses.Length / JointCount;
        SampleRateHz = float.IsFinite(sampleRateHz) && sampleRateHz > 1.0f
            ? sampleRateHz
            : SomaModelContract.SampleRateHz;
        DurationSeconds = MathF.Max(0.0f, (FrameCount - 1) / SampleRateHz);
    }

    /// <summary>
    /// Samples one placed 30-joint pose at <paramref name="timeSeconds"/> anchored at
    /// <paramref name="anchorRootXZ"/> facing <paramref name="facingYawRadians"/> without heap allocations.
    /// When <paramref name="loop"/> is true, cross-fades the final <c>0.25 s</c> back into frame 0
    /// so repeated idle playback loops smoothly without a seam.
    /// </summary>
    public void SamplePlacedFrame(
        float timeSeconds,
        bool loop,
        Vector3 anchorRootXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Destination span must hold {JointCount} joints.", nameof(destination));

        float t = float.IsFinite(timeSeconds) && timeSeconds > 0.0f ? timeSeconds : 0.0f;
        if (DurationSeconds <= 1.0e-5f || FrameCount == 1)
        {
            CopyPlacedSingleFrame(0, anchorRootXZ, facingYawRadians, destination);
            return;
        }

        if (loop)
            t %= DurationSeconds;
        else
            t = MathF.Min(t, DurationSeconds);

        SampleLocalInterpolated(t, destination);

        // Smoothly cross-fade the final 0.25 s of a looping clip back into frame 0 to hide any loop seam.
        if (loop && DurationSeconds > LoopCrossfadeSeconds * 2.5f)
        {
            float fadeStart = DurationSeconds - LoopCrossfadeSeconds;
            if (t > fadeStart)
            {
                float u = Math.Clamp((t - fadeStart) / LoopCrossfadeSeconds, 0.0f, 1.0f);
                float w = u * u * (3.0f - 2.0f * u);
                for (int joint = 0; joint < JointCount; joint++)
                    destination[joint] = SomaPoseTransform.Lerp(destination[joint], Poses[joint], w);
            }
        }

        PlaceRootInPlace(anchorRootXZ, facingYawRadians, destination);
    }

    /// <summary>
    /// Extracts four consecutive 30 Hz target poses starting at <paramref name="startTimeSeconds"/>
    /// and places them at <paramref name="anchorRootXZ"/> facing <paramref name="facingYawRadians"/>
    /// for one-shot SOMA clip-entry transition generation.
    /// </summary>
    public void CopyPlacedFourPoseToken(
        float startTimeSeconds,
        Vector3 anchorRootXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination)
    {
        if (destination.Length < SomaModelContract.TargetPoseCapacity * JointCount)
            throw new ArgumentException("Destination span must hold 4 poses x 30 joints.", nameof(destination));

        const float stepSeconds = 1.0f / SomaModelContract.SampleRateHz;
        for (int frame = 0; frame < SomaModelContract.TargetPoseCapacity; frame++)
        {
            SamplePlacedFrame(
                startTimeSeconds + frame * stepSeconds,
                loop: false,
                anchorRootXZ,
                facingYawRadians,
                destination.Slice(frame * JointCount, JointCount));
        }
    }

    private void SampleLocalInterpolated(float timeSeconds, Span<SomaPoseTransform> destination)
    {
        float framePos = timeSeconds * SampleRateHz;
        int f0 = Math.Clamp((int)MathF.Floor(framePos), 0, FrameCount - 1);
        int f1 = Math.Min(f0 + 1, FrameCount - 1);
        float blend = f0 == f1 ? 0.0f : Math.Clamp(framePos - f0, 0.0f, 1.0f);

        int base0 = f0 * JointCount;
        int base1 = f1 * JointCount;
        for (int joint = 0; joint < JointCount; joint++)
            destination[joint] = SomaPoseTransform.Lerp(Poses[base0 + joint], Poses[base1 + joint], blend);
    }

    private void CopyPlacedSingleFrame(
        int frameIndex,
        Vector3 anchorRootXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> destination)
    {
        Poses.AsSpan(frameIndex * JointCount, JointCount).CopyTo(destination);
        PlaceRootInPlace(anchorRootXZ, facingYawRadians, destination);
    }

    private static void PlaceRootInPlace(
        Vector3 anchorRootXZ,
        float facingYawRadians,
        Span<SomaPoseTransform> pose)
    {
        Quaternion facingRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, facingYawRadians);
        SomaPoseTransform root = pose[0];
        Vector3 rotatedPos = Vector3.Transform(root.Position, facingRot);
        var placedPos = new Vector3(
            anchorRootXZ.X + rotatedPos.X,
            root.Position.Y,
            anchorRootXZ.Z + rotatedPos.Z);
        Quaternion placedRot = SomaPoseTransform.NormalizeOrIdentity(facingRot * root.Rotation);
        pose[0] = new SomaPoseTransform(placedPos, placedRot);
    }
}

using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Preallocated 30 Hz SOMA trajectory playback buffer with smooth inertial splice
/// cross-blending when a newly replanned trajectory arrives from the async worker.
/// </summary>
public sealed class SomaTrajectoryPlaybackBuffer
{
    private const int JointCount = SomaModelContract.JointCount;

    public SomaPoseTransform[] Poses { get; } =
        new SomaPoseTransform[SomaModelContract.MaxValidFrameCount * JointCount];

    public float[] FootContacts { get; } =
        new float[SomaModelContract.MaxValidFrameCount * SomaModelContract.FootContactCount];

    private readonly Quaternion[] _spliceDeltaRotations = new Quaternion[JointCount];
    private float _spliceDeltaRootY;
    private Vector2 _spliceDeltaRootVelocityXZ;
    private Vector2 _spliceRootOffsetXZ;
    private Vector2 _lastRootPositionXZ;
    private Vector2 _lastRootVelocityXZ;
    private bool _hasLastRootPositionXZ;
    private Vector4 _spliceDeltaFootContacts;
    private float _spliceElapsedSeconds;
    private float _spliceDurationSeconds;
    private bool _isSplicing;

    public int FrameCount { get; private set; }
    public float PlaybackTimeSeconds { get; private set; }
    public long GenerationSequence { get; private set; }
    /// <summary>
    /// Most recent planar root velocity in SOMA space computed during playback.
    /// Used to preserve C1 velocity continuity across async trajectory replan swaps.
    /// </summary>
    public Vector2 LastRootVelocityXZ => _lastRootVelocityXZ;

    public int RemainingFrames
    {
        get
        {
            if (FrameCount <= 0)
                return 0;

            int currentFrame = Math.Clamp((int)MathF.Floor(PlaybackTimeSeconds * SomaModelContract.SampleRateHz), 0, FrameCount - 1);
            return Math.Max(0, FrameCount - 1 - currentFrame);
        }
    }

    public bool IsExhausted => FrameCount <= 0 || PlaybackTimeSeconds >= (FrameCount - 1) / SomaModelContract.SampleRateHz;

    public void Clear()
    {
        FrameCount = 0;
        PlaybackTimeSeconds = 0.0f;
        GenerationSequence = 0L;
        _isSplicing = false;
        _spliceElapsedSeconds = 0.0f;
        _spliceDurationSeconds = 0.0f;
        _spliceDeltaRootVelocityXZ = Vector2.Zero;
        _spliceRootOffsetXZ = Vector2.Zero;
        _lastRootPositionXZ = Vector2.Zero;
        _lastRootVelocityXZ = Vector2.Zero;
        _hasLastRootPositionXZ = false;
    }

    /// <summary>
    /// Copies a decoded output buffer into this trajectory snapshot.
    /// </summary>
    public void CopyFromDecodedOutput(SomaInferenceOutputBuffer output, long sequence)
    {
        int validFrames = Math.Clamp(output.ValidFrameCount, 0, SomaModelContract.MaxValidFrameCount);
        if (validFrames > 0)
        {
            output.DecodedLocalPoses.AsSpan(0, validFrames * JointCount).CopyTo(Poses);
            output.FootContacts.AsSpan(0, validFrames * SomaModelContract.FootContactCount).CopyTo(FootContacts);
        }

        FrameCount = validFrames;
        PlaybackTimeSeconds = 0.0f;
        GenerationSequence = sequence;
        _spliceDeltaRootVelocityXZ = Vector2.Zero;
        _spliceRootOffsetXZ = Vector2.Zero;
        _lastRootPositionXZ = Vector2.Zero;
        _lastRootVelocityXZ = Vector2.Zero;
        _hasLastRootPositionXZ = false;
    }

    /// <summary>
    /// Captures rotational, vertical root, and planar root velocity deltas from the previously
    /// displayed pose so the newly swapped trajectory decays smoothly via C1 inertialization
    /// without velocity lurches across async replans.
    /// </summary>
    public void BeginSpliceFrom(
        ReadOnlySpan<SomaPoseTransform> previousDisplayedPose,
        Vector4 previousFootContacts,
        float spliceDurationSeconds,
        Vector2 previousRootVelocityXZ = default)
    {
        _spliceRootOffsetXZ = Vector2.Zero;
        _spliceDeltaRootVelocityXZ = Vector2.Zero;

        if (FrameCount <= 0 || previousDisplayedPose.Length < JointCount || spliceDurationSeconds <= 1.0e-5f)
        {
            _isSplicing = false;
            return;
        }

        float framePosition = PlaybackTimeSeconds * SomaModelContract.SampleRateHz;
        int frame0 = Math.Clamp((int)MathF.Floor(framePosition), 0, FrameCount - 1);
        int frame1 = Math.Min(frame0 + 1, FrameCount - 1);
        float blend = frame0 == frame1 ? 0.0f : Math.Clamp(framePosition - frame0, 0.0f, 1.0f);

        int base0 = frame0 * JointCount;
        int base1 = frame1 * JointCount;
        Vector3 currentRootPos = Vector3.Lerp(Poses[base0].Position, Poses[base1].Position, blend);
        _spliceDeltaRootY = previousDisplayedPose[0].Position.Y - currentRootPos.Y;

        // Capture planar root velocity difference at the splice boundary so root speed glides
        // smoothly into the new trajectory instead of stepping abruptly on replan swaps.
        if (float.IsFinite(previousRootVelocityXZ.X) && float.IsFinite(previousRootVelocityXZ.Y) && FrameCount >= 2)
        {
            int velFrame0 = frame0 == frame1 ? Math.Max(0, frame0 - 1) : frame0;
            int velFrame1 = Math.Min(velFrame0 + 1, FrameCount - 1);
            Vector3 p0 = Poses[velFrame0 * JointCount].Position;
            Vector3 p1 = Poses[velFrame1 * JointCount].Position;
            Vector2 newStartVelXZ = new Vector2(p1.X - p0.X, p1.Z - p0.Z) * SomaModelContract.SampleRateHz;
            Vector2 velDelta = previousRootVelocityXZ - newStartVelXZ;
            float maxVelDelta = 3.0f;
            float velDeltaLenSq = velDelta.LengthSquared();
            if (velDeltaLenSq > maxVelDelta * maxVelDelta)
                velDelta *= maxVelDelta / MathF.Sqrt(velDeltaLenSq);
            _spliceDeltaRootVelocityXZ = velDelta;
        }

        _lastRootPositionXZ = new Vector2(previousDisplayedPose[0].Position.X, previousDisplayedPose[0].Position.Z);
        _lastRootVelocityXZ = previousRootVelocityXZ;
        _hasLastRootPositionXZ = true;

        for (int joint = 0; joint < JointCount; joint++)
        {
            Quaternion currentRot = SomaPoseTransform.NormalizeOrIdentity(
                Quaternion.Slerp(Poses[base0 + joint].Rotation, Poses[base1 + joint].Rotation, blend));
            Quaternion previousRot = SomaPoseTransform.NormalizeOrIdentity(previousDisplayedPose[joint].Rotation);
            Quaternion deltaRot = SomaPoseTransform.NormalizeOrIdentity(Quaternion.Inverse(currentRot) * previousRot);
            if (deltaRot.W < 0.0f)
                deltaRot = new Quaternion(-deltaRot.X, -deltaRot.Y, -deltaRot.Z, -deltaRot.W);

            _spliceDeltaRotations[joint] = deltaRot;
        }

        int c0 = frame0 * SomaModelContract.FootContactCount;
        int c1 = frame1 * SomaModelContract.FootContactCount;
        var currentContacts = new Vector4(
            FootContacts[c0 + 0] + (FootContacts[c1 + 0] - FootContacts[c0 + 0]) * blend,
            FootContacts[c0 + 1] + (FootContacts[c1 + 1] - FootContacts[c0 + 1]) * blend,
            FootContacts[c0 + 2] + (FootContacts[c1 + 2] - FootContacts[c0 + 2]) * blend,
            FootContacts[c0 + 3] + (FootContacts[c1 + 3] - FootContacts[c0 + 3]) * blend);
        _spliceDeltaFootContacts = previousFootContacts - currentContacts;
        _spliceElapsedSeconds = 0.0f;
        _spliceDurationSeconds = spliceDurationSeconds;
        _isSplicing = true;
    }

    /// <summary>
    /// Applies a world-space root translation offset across all buffered frames so physical
    /// capsule clamping stays synchronized with playback.
    /// </summary>
    public void ApplyRootTranslationOffset(Vector3 offset)
    {
        if (FrameCount <= 0 || offset == Vector3.Zero)
            return;

        for (int f = 0; f < FrameCount; f++)
        {
            int rootIndex = f * JointCount;
            SomaPoseTransform root = Poses[rootIndex];
            Poses[rootIndex] = new SomaPoseTransform(root.Position + offset, root.Rotation);
        }

        if (_hasLastRootPositionXZ)
            _lastRootPositionXZ += new Vector2(offset.X, offset.Z);
    }

    /// <summary>
    /// Advances a newly swapped trajectory past asynchronous worker latency and aligns its
    /// planar root position to <paramref name="currentDisplayedRoot"/> so trajectory swaps never
    /// pull the character root backward into the physics controller.
    /// </summary>
    public void AdvanceToLatencyAndAlignRootXZ(float latencySeconds, Vector3 currentDisplayedRoot)
    {
        if (FrameCount <= 0)
            return;

        float maxTime = (FrameCount - 1) / SomaModelContract.SampleRateHz;
        if (float.IsFinite(latencySeconds) && latencySeconds > 0.0f && maxTime > 0.12f)
            PlaybackTimeSeconds = Math.Clamp(latencySeconds, 0.0f, MathF.Min(0.25f, maxTime - 0.10f));

        float framePosition = PlaybackTimeSeconds * SomaModelContract.SampleRateHz;
        int frame0 = Math.Clamp((int)MathF.Floor(framePosition), 0, FrameCount - 1);
        int frame1 = Math.Min(frame0 + 1, FrameCount - 1);
        float blend = frame0 == frame1 ? 0.0f : Math.Clamp(framePosition - frame0, 0.0f, 1.0f);

        Vector3 sampledRoot = Vector3.Lerp(
            Poses[frame0 * JointCount].Position,
            Poses[frame1 * JointCount].Position,
            blend);
        Vector3 alignOffset = new(
            currentDisplayedRoot.X - sampledRoot.X,
            0.0f,
            currentDisplayedRoot.Z - sampledRoot.Z);
        if (alignOffset.LengthSquared() > 1.0e-8f)
            ApplyRootTranslationOffset(alignOffset);
    }

    /// <summary>
    /// Advances playback time by <paramref name="deltaSeconds"/> and writes the interpolated
    /// (and optionally splice-blended) pose and foot contacts into the destination buffers.
    /// </summary>
    public bool TryAdvanceAndSample(
        float deltaSeconds,
        Span<SomaPoseTransform> destinationPose,
        out Vector4 footContacts)
    {
        if (FrameCount <= 0)
        {
            footContacts = Vector4.Zero;
            return false;
        }

        float validDt = float.IsFinite(deltaSeconds) && deltaSeconds > 0.0f ? deltaSeconds : 0.0f;
        if (validDt > 0.0f)
        {
            float maxTime = (FrameCount - 1) / SomaModelContract.SampleRateHz;
            PlaybackTimeSeconds = MathF.Min(PlaybackTimeSeconds + validDt, maxTime);
            if (_isSplicing)
            {
                float midTime = Math.Clamp(
                    (_spliceElapsedSeconds + validDt * 0.5f) / MathF.Max(_spliceDurationSeconds, 1.0e-5f),
                    0.0f,
                    1.0f);
                float midDecay = 1.0f - (midTime * midTime * (3.0f - 2.0f * midTime));
                float remainingSpliceDt = MathF.Max(0.0f, MathF.Min(validDt, _spliceDurationSeconds - _spliceElapsedSeconds));
                _spliceRootOffsetXZ += _spliceDeltaRootVelocityXZ * (midDecay * remainingSpliceDt);

                _spliceElapsedSeconds += validDt;
                if (_spliceElapsedSeconds >= _spliceDurationSeconds)
                    _isSplicing = false;
            }
        }

        float framePosition = PlaybackTimeSeconds * SomaModelContract.SampleRateHz;
        int frame0 = Math.Clamp((int)MathF.Floor(framePosition), 0, FrameCount - 1);
        int frame1 = Math.Min(frame0 + 1, FrameCount - 1);
        float blend = frame0 == frame1 ? 0.0f : Math.Clamp(framePosition - frame0, 0.0f, 1.0f);

        int base0 = frame0 * JointCount;
        int base1 = frame1 * JointCount;

        // Use Catmull-Rom cubic interpolation for root translation to eliminate 30 Hz linear
        // velocity corners while keeping exact endpoint tracking.
        int framePrev = Math.Max(0, frame0 - 1);
        int frameNext = Math.Min(FrameCount - 1, frame1 + 1);
        Vector3 rootPosition = frame0 == frame1
            ? Poses[base0].Position
            : EvaluateCatmullRom(
                Poses[framePrev * JointCount].Position,
                Poses[base0].Position,
                Poses[base1].Position,
                Poses[frameNext * JointCount].Position,
                blend);
        rootPosition = new Vector3(
            rootPosition.X + _spliceRootOffsetXZ.X,
            rootPosition.Y,
            rootPosition.Z + _spliceRootOffsetXZ.Y);
        Quaternion rootRotation = SomaPoseTransform.NormalizeOrIdentity(
            Quaternion.Slerp(Poses[base0].Rotation, Poses[base1].Rotation, blend));
        destinationPose[0] = new SomaPoseTransform(rootPosition, rootRotation);

        for (int joint = 1; joint < JointCount; joint++)
            destinationPose[joint] = SomaPoseTransform.Lerp(Poses[base0 + joint], Poses[base1 + joint], blend);

        int c0 = frame0 * SomaModelContract.FootContactCount;
        int c1 = frame1 * SomaModelContract.FootContactCount;
        footContacts = new Vector4(
            FootContacts[c0 + 0] + (FootContacts[c1 + 0] - FootContacts[c0 + 0]) * blend,
            FootContacts[c0 + 1] + (FootContacts[c1 + 1] - FootContacts[c0 + 1]) * blend,
            FootContacts[c0 + 2] + (FootContacts[c1 + 2] - FootContacts[c0 + 2]) * blend,
            FootContacts[c0 + 3] + (FootContacts[c1 + 3] - FootContacts[c0 + 3]) * blend);

        if (_isSplicing && _spliceDurationSeconds > 1.0e-5f)
        {
            float rawT = Math.Clamp(_spliceElapsedSeconds / _spliceDurationSeconds, 0.0f, 1.0f);
            float decay = 1.0f - (rawT * rawT * (3.0f - 2.0f * rawT));

            Vector3 splicedRootPos = new(
                destinationPose[0].Position.X,
                destinationPose[0].Position.Y + _spliceDeltaRootY * decay,
                destinationPose[0].Position.Z);
            Quaternion rootDelta = Quaternion.Slerp(Quaternion.Identity, _spliceDeltaRotations[0], decay);
            destinationPose[0] = new SomaPoseTransform(
                splicedRootPos,
                SomaPoseTransform.NormalizeOrIdentity(destinationPose[0].Rotation * rootDelta));

            for (int joint = 1; joint < JointCount; joint++)
            {
                Quaternion jointDelta = Quaternion.Slerp(Quaternion.Identity, _spliceDeltaRotations[joint], decay);
                destinationPose[joint] = new SomaPoseTransform(
                    destinationPose[joint].Position,
                    SomaPoseTransform.NormalizeOrIdentity(destinationPose[joint].Rotation * jointDelta));
            }

            footContacts = Vector4.Clamp(footContacts + _spliceDeltaFootContacts * decay, Vector4.Zero, Vector4.One);
        }

        Vector2 finalRootXZ = new(destinationPose[0].Position.X, destinationPose[0].Position.Z);
        if (_hasLastRootPositionXZ && validDt > 1.0e-5f)
            _lastRootVelocityXZ = (finalRootXZ - _lastRootPositionXZ) / validDt;
        _lastRootPositionXZ = finalRootXZ;
        _hasLastRootPositionXZ = true;

        return true;
    }

    private static Vector3 EvaluateCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            (2.0f * p1)
            + (-p0 + p2) * t
            + (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * t2
            + (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * t3);
    }

    /// <summary>
    /// Samples a future pose at <c>PlaybackTimeSeconds + offsetSeconds</c> without advancing
    /// playback state. Used by debug visualization to display future predicted skeletons.
    /// </summary>
    public bool TrySampleFuturePose(
        float offsetSeconds,
        Span<SomaPoseTransform> destinationPose,
        out float actualLeadSeconds)
    {
        actualLeadSeconds = 0.0f;
        if (FrameCount <= 0 || destinationPose.Length < JointCount)
            return false;

        float maxTime = (FrameCount - 1) / SomaModelContract.SampleRateHz;
        float targetTime = Math.Clamp(PlaybackTimeSeconds + MathF.Max(0.0f, offsetSeconds), 0.0f, maxTime);
        actualLeadSeconds = MathF.Max(0.0f, targetTime - PlaybackTimeSeconds);

        float framePosition = targetTime * SomaModelContract.SampleRateHz;
        int frame0 = Math.Clamp((int)MathF.Floor(framePosition), 0, FrameCount - 1);
        int frame1 = Math.Min(frame0 + 1, FrameCount - 1);
        float blend = frame0 == frame1 ? 0.0f : Math.Clamp(framePosition - frame0, 0.0f, 1.0f);

        int base0 = frame0 * JointCount;
        int base1 = frame1 * JointCount;
        for (int joint = 0; joint < JointCount; joint++)
            destinationPose[joint] = SomaPoseTransform.Lerp(Poses[base0 + joint], Poses[base1 + joint], blend);

        if (_spliceRootOffsetXZ != Vector2.Zero)
        {
            SomaPoseTransform root = destinationPose[0];
            destinationPose[0] = new SomaPoseTransform(
                new Vector3(root.Position.X + _spliceRootOffsetXZ.X, root.Position.Y, root.Position.Z + _spliceRootOffsetXZ.Y),
                root.Rotation);
        }

        return true;
    }
}

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

    private readonly SomaPoseTransform[] _spliceStartPose = new SomaPoseTransform[JointCount];
    private Vector4 _spliceStartFootContacts;
    private float _spliceElapsedSeconds;
    private float _spliceDurationSeconds;
    private bool _isSplicing;

    public int FrameCount { get; private set; }
    public float PlaybackTimeSeconds { get; private set; }
    public long GenerationSequence { get; private set; }

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
    }

    /// <summary>
    /// Begins a short cross-blend from the previously displayed pose into frame 0 of this
    /// newly swapped trajectory so background inference latency produces no visual snap.
    /// </summary>
    public void BeginSpliceFrom(
        ReadOnlySpan<SomaPoseTransform> previousDisplayedPose,
        Vector4 previousFootContacts,
        float spliceDurationSeconds)
    {
        if (previousDisplayedPose.Length < JointCount || spliceDurationSeconds <= 1.0e-5f)
        {
            _isSplicing = false;
            return;
        }

        previousDisplayedPose.Slice(0, JointCount).CopyTo(_spliceStartPose);
        _spliceStartFootContacts = previousFootContacts;
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

        if (_isSplicing)
            _spliceStartPose[0] = new SomaPoseTransform(_spliceStartPose[0].Position + offset, _spliceStartPose[0].Rotation);
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

        if (float.IsFinite(deltaSeconds) && deltaSeconds > 0.0f)
        {
            float maxTime = (FrameCount - 1) / SomaModelContract.SampleRateHz;
            PlaybackTimeSeconds = MathF.Min(PlaybackTimeSeconds + deltaSeconds, maxTime);
            if (_isSplicing)
            {
                _spliceElapsedSeconds += deltaSeconds;
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
        for (int joint = 0; joint < JointCount; joint++)
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
            // Smoothstep curve for gentle acceleration/deceleration across the splice window.
            float spliceWeight = rawT * rawT * (3.0f - 2.0f * rawT);
            for (int joint = 0; joint < JointCount; joint++)
                destinationPose[joint] = SomaPoseTransform.Lerp(_spliceStartPose[joint], destinationPose[joint], spliceWeight);

            footContacts = Vector4.Lerp(_spliceStartFootContacts, footContacts, spliceWeight);
        }

        return true;
    }
}

using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Stage 2 SOMA locomotion driver that runs a TensorRT <see cref="IMotionBricksInferenceSession"/>
/// when supplied, decodes 30 Hz SOMA frames, interpolates to render frame rate, and writes
/// canonical humanoid muscles and Body root poses into <see cref="MotionBricksEvaluatedPose"/>.
/// Never silently substitutes CPU inference when a TensorRT session fails.
/// </summary>
public sealed class SomaFixedTargetLocomotionDriver : IMotionBricksLocomotionDriver
{
    private readonly IMotionBricksInferenceSession? _inferenceSession;
    private readonly bool _ownsSession;
    private readonly SomaRestSkeletonCalibration _calibration;
    private readonly SomaToHumanoidMuscleConverter _converter;
    private readonly SomaInferenceInputBuffer _inputBuffer = new();
    private readonly SomaInferenceOutputBuffer _outputBuffer = new();
    private readonly SomaPoseTransform[] _contextPoses =
        new SomaPoseTransform[SomaModelContract.ContextPoseCount * SomaModelContract.JointCount];
    private readonly SomaPoseTransform[] _targetPoses =
        new SomaPoseTransform[SomaModelContract.TargetPoseCapacity * SomaModelContract.JointCount];
    private readonly SomaPoseTransform[] _interpolatedPose = new SomaPoseTransform[SomaModelContract.JointCount];

    private float _playbackTimeSeconds;
    private float _avatarLegScale = 1.0f;
    private bool _hasCanonicalOrigin;
    private Vector3 _canonicalBodyPosition;
    private Quaternion _canonicalBodyRotation = Quaternion.Identity;
    private bool _disposed;

    public SomaFixedTargetLocomotionDriver(
        IMotionBricksInferenceSession? inferenceSession,
        bool ownsSession = false,
        SomaRestSkeletonCalibration? calibration = null,
        float avatarLegScale = 1.0f)
    {
        _inferenceSession = inferenceSession;
        _ownsSession = ownsSession;
        _calibration = calibration ?? SomaRestSkeletonCalibration.CanonicalDefault;
        _converter = new SomaToHumanoidMuscleConverter(_calibration);
        _avatarLegScale = float.IsFinite(avatarLegScale) && avatarLegScale > 0.01f ? avatarLegScale : 1.0f;

        SeedNeutralContext();
        if (_inferenceSession is null)
            DiagnosticStatus = "No MotionBricks TensorRT inference session is bound.";
        else if (_inferenceSession.IsEngineReady)
            DiagnosticStatus = "Ready (TensorRT session active).";
        else
            DiagnosticStatus = "TensorRT session is bound but its engine has not been built or loaded.";
    }

    public bool IsReady => !_disposed && _inferenceSession is { IsEngineReady: true };

    public string DiagnosticStatus { get; private set; }

    public float AvatarLegScale
    {
        get => _avatarLegScale;
        set => _avatarLegScale = float.IsFinite(value) && value > 0.01f ? value : 1.0f;
    }

    /// <summary>
    /// Exposes the active decoded 30 Hz SOMA trajectory buffer for diagnostics and testing.
    /// </summary>
    public SomaInferenceOutputBuffer OutputBuffer => _outputBuffer;

    public void OnStateEntered()
    {
        _playbackTimeSeconds = 0.0f;
        _hasCanonicalOrigin = false;
    }

    public void OnStateExited()
    {
    }

    public void ResetPlayback()
    {
        _playbackTimeSeconds = 0.0f;
        _hasCanonicalOrigin = false;
        _outputBuffer.Clear();
        SeedNeutralContext();
    }

    /// <summary>
    /// Executes one fixed-target SOMA inference pass using the bound TensorRT session,
    /// populating the 30 Hz playback trajectory buffer.
    /// </summary>
    public bool TryGenerateFixedForwardTarget(
        float forwardDistanceMeters,
        float targetYawRadians,
        in SomaInferenceConfig config,
        out string? error)
    {
        if (_disposed)
        {
            error = "MotionBricks driver has been disposed.";
            DiagnosticStatus = error;
            return false;
        }

        if (_inferenceSession is null || !_inferenceSession.IsEngineReady)
        {
            error = "TensorRT inference session is not ready. Stage TensorRT and SOMA weights, then build or load the engine.";
            DiagnosticStatus = error;
            return false;
        }

        BuildFixedTargetPose(forwardDistanceMeters, targetYawRadians);
        SomaTensorCodec.EncodeInputs(
            _contextPoses,
            _targetPoses,
            targetPoseCount: 1,
            config,
            SomaPoseTransform.Identity,
            _inputBuffer);

        if (!_inferenceSession.TryRunSomaInference(_inputBuffer, _outputBuffer, out error))
        {
            DiagnosticStatus = error ?? "TensorRT SOMA inference failed.";
            return false;
        }

        int validFrames = SomaTensorCodec.DecodeOutputs(
            _outputBuffer,
            _inputBuffer.GuideAlignment,
            SomaPoseTransform.Identity,
            _calibration.ReferenceLocalPoses);
        if (validFrames <= 0)
        {
            error = "SOMA output decoded zero valid frames.";
            DiagnosticStatus = error;
            return false;
        }

        if (config.CorrectGeneratedDiff)
        {
            ReadOnlySpan<SomaPoseTransform> lastContext = _contextPoses.AsSpan(
                (SomaModelContract.ContextPoseCount - 1) * SomaModelContract.JointCount,
                SomaModelContract.JointCount);
            ReadOnlySpan<SomaPoseTransform> firstTarget = _targetPoses.AsSpan(0, SomaModelContract.JointCount);
            SomaTensorCodec.ApplyEndpointCorrection(lastContext, firstTarget, _outputBuffer);
        }

        _playbackTimeSeconds = 0.0f;
        DiagnosticStatus = $"Ready ({validFrames} frames at 30 Hz).";
        error = null;
        return true;
    }

    public void AdvanceAndEvaluate(in MotionBricksSteeringInput input, MotionBricksEvaluatedPose destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_disposed)
        {
            destination.HasValidPose = false;
            return;
        }

        if (_outputBuffer.ValidFrameCount <= 0 && IsReady)
        {
            float moveMag = Math.Clamp(input.MoveInput.Length(), 0.0f, 1.0f);
            float speed = moveMag <= 0.05f
                ? 0.0f
                : moveMag < 0.6f
                    ? input.WalkSpeedMetersPerSecond * (moveMag / 0.6f)
                    : input.RunSpeedMetersPerSecond;
            float horizonDistance = speed * 1.0f;
            float targetYaw = moveMag > 0.05f
                ? MathF.Atan2(input.MoveInput.X, input.MoveInput.Y)
                : input.DesiredFacingYawRadians;
            TryGenerateFixedForwardTarget(horizonDistance, targetYaw, SomaInferenceConfig.Default, out _);
        }

        int frameCount = _outputBuffer.ValidFrameCount;
        if (frameCount <= 0)
        {
            destination.HasValidPose = false;
            return;
        }

        if (float.IsFinite(input.DeltaSeconds) && input.DeltaSeconds > 0.0f)
            _playbackTimeSeconds += input.DeltaSeconds;

        float framePosition = _playbackTimeSeconds * SomaModelContract.SampleRateHz;
        int frame0 = Math.Clamp((int)MathF.Floor(framePosition), 0, frameCount - 1);
        int frame1 = Math.Min(frame0 + 1, frameCount - 1);
        float blend = frame0 == frame1 ? 0.0f : Math.Clamp(framePosition - frame0, 0.0f, 1.0f);

        ReadOnlySpan<SomaPoseTransform> pose0 = _outputBuffer.GetFrameLocalPose(frame0);
        ReadOnlySpan<SomaPoseTransform> pose1 = _outputBuffer.GetFrameLocalPose(frame1);
        for (int joint = 0; joint < SomaModelContract.JointCount; joint++)
            _interpolatedPose[joint] = SomaPoseTransform.Lerp(pose0[joint], pose1[joint], blend);

        Array.Clear(destination.MuscleValues);
        _converter.ConvertFrame(
            _interpolatedPose,
            _avatarLegScale,
            destination.MuscleValues,
            out Vector3 bodyPosition,
            out Quaternion bodyRotation);

        if (!_hasCanonicalOrigin)
        {
            _canonicalBodyPosition = bodyPosition;
            _canonicalBodyRotation = bodyRotation;
            _hasCanonicalOrigin = true;
        }

        destination.CanonicalBodyPosition = _canonicalBodyPosition;
        destination.CanonicalBodyRotation = _canonicalBodyRotation;
        destination.CurrentBodyPosition = bodyPosition;
        destination.CurrentBodyRotation = bodyRotation;

        ReadOnlySpan<float> contacts0 = _outputBuffer.GetFrameFootContacts(frame0);
        ReadOnlySpan<float> contacts1 = _outputBuffer.GetFrameFootContacts(frame1);
        destination.FootContacts = new Vector4(
            contacts0[0] + (contacts1[0] - contacts0[0]) * blend,
            contacts0[1] + (contacts1[1] - contacts0[1]) * blend,
            contacts0[2] + (contacts1[2] - contacts0[2]) * blend,
            contacts0[3] + (contacts1[3] - contacts0[3]) * blend);
        destination.HasValidPose = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_ownsSession)
            _inferenceSession?.Dispose();
    }

    private void SeedNeutralContext()
    {
        SomaPoseTransform[] reference = _calibration.ReferenceLocalPoses;
        for (int frame = 0; frame < SomaModelContract.ContextPoseCount; frame++)
            reference.CopyTo(_contextPoses, frame * SomaModelContract.JointCount);
    }

    private void BuildFixedTargetPose(float forwardDistanceMeters, float targetYawRadians)
    {
        SomaPoseTransform[] reference = _calibration.ReferenceLocalPoses;
        reference.CopyTo(_targetPoses, 0);

        int lastContextBase = (SomaModelContract.ContextPoseCount - 1) * SomaModelContract.JointCount;
        SomaPoseTransform startRoot = _contextPoses[lastContextBase];
        Quaternion targetRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, targetYawRadians);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, targetRotation);
        Vector3 targetPosition = startRoot.Position + forward * forwardDistanceMeters;
        _targetPoses[0] = new SomaPoseTransform(targetPosition, targetRotation);
    }
}

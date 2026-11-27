using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Production <see cref="IMotionBricksLocomotionDriver"/> that runs Smart Locomotion
/// planning on the animation thread and dispatches TensorRT SOMA replans on a dedicated
/// background worker thread with double-buffered trajectory swaps and splice cross-blending.
/// </summary>
public sealed partial class MotionBricksAsyncLocomotionDriver : IMotionBricksLocomotionDriver
{
    private const int JointCount = SomaModelContract.JointCount;
    private const float IdleCrossfadeDurationSeconds = 0.55f;
    private const int IdleCrossfadeStartRemainingFrames = 15;
    private const float IdleCrossfadeFallbackDelaySeconds = 0.85f;
    private const float InitialSpawnIdleDelaySeconds = 0.12f;
    private const float StandToProneDurationSeconds = 0.62f;
    private const float CrouchToProneDurationSeconds = 0.44f;
    private const float ProneToCrouchDurationSeconds = 0.42f;
    private const float ProneToStandDurationSeconds = 0.60f;
    private const float AirborneConfirmDelaySeconds = 0.06f;
    private const float JumpTakeoffVelocityThreshold = 1.2f;
    private const float BodyRotationSmoothingRate = 28.0f;

    private volatile IMotionBricksInferenceSession? _inferenceSession;
    private Task<(IMotionBricksInferenceSession? Session, string? Error)>? _pendingSessionTask;
    private volatile bool _deferredSessionPending;
    private volatile bool _deferredSessionFailed;
    private readonly bool _ownsSession;
    private readonly bool _useBackgroundThread;
    private readonly SomaRestSkeletonCalibration _calibration;
    private readonly SomaSmartLocomotionSettings _settings;
    private readonly SomaLocomotionPoseBank _poseBank;
    private readonly SomaSmartLocomotionPlanner _planner;
    private readonly SomaToHumanoidMuscleConverter _converter;
    private readonly HumanoidToSomaPoseSampler _poseSampler;
    private readonly SomaContextHistoryBuffer _history = new();
    private readonly SomaPoseTransform[] _seedSomaPoses =
        new SomaPoseTransform[SomaModelContract.ContextPoseCount * JointCount];

    private readonly SomaPoseTransform[] _displayedPose = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _airborneScratch = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _somaIdleScratch = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _debugPoseScratch = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _debugWorldScratch = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _idleCrossfadeStartPose = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _landingContextScratch =
        new SomaPoseTransform[SomaModelContract.ContextPoseCount * JointCount];
    private readonly object _debugSync = new();
    private readonly SomaPoseTransform[] _debugDisplayedPoseSnapshot = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _debugFutureMidPoseSnapshot = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _debugFutureEndPoseSnapshot = new SomaPoseTransform[JointCount];
    private readonly MotionBricksTrajectoryPoint[] _debugTrajectorySnapshot =
        new MotionBricksTrajectoryPoint[MotionBricksDebugSnapshot.MaxTrajectoryPoints];
    private int _debugTrajectoryCount;
    private Vector4 _debugFootContactsSnapshot;
    private float _debugFutureMidLeadSeconds;
    private float _debugFutureEndLeadSeconds;
    private bool _debugHasCurrentPose;
    private bool _debugHasFutureMidPose;
    private bool _debugHasFutureEndPose;
    private Vector3 _debugWorldFootAnchor;
    private Vector3 _debugTargetGroundWorld;
    private Vector3 _liveWorldFootPosition;
    private Vector3 _liveActualPlanarVelocity;
    private bool _hasLiveWorldFootPosition;
    private Vector3 _somaWorldOriginOffsetXZ;
    private bool _hasSomaWorldOriginOffset;
    private float _trajectoryPreviewAccumulator;
    private volatile IMotionBricksTrajectoryProber? _trajectoryProber;

    private Vector4 _displayedFootContacts;
    private bool _hasDisplayedPose;
    private bool _wasGrounded = true;
    private float _ungroundedDurationSeconds;
    private bool _wasProne;
    private float _proneTransitionProgress;
    private bool _proneTransitionUseStandingEndpoint = true;
    private int _lastNonProneStyleIndex = SomaLocomotionPoseBank.StandingStyleIndex;
    private float _proneCrawlPhaseRadians;
    private float _lastProneFacingYawRadians;
    private bool _hasLastProneFacingYaw;
    private bool _isInBreathingIdle;
    private bool _isCrossfadingToIdle;
    private float _idleSettledSeconds;
    private float _idleCrossfadeElapsedSeconds;
    private float _idleBreathingTimeSeconds;
    private int _idleStyleIndex = SomaLocomotionPoseBank.StandingStyleIndex;
    private SomaMotionClip? _activeIdleClip;
    private uint _idleClipRngState = 0x13579BDFu;
    private bool _hasQueuedStoppingReplan;
    private long _stoppingReplanSequence;
    private Vector3 _idleAnchorRootXZ;
    private float _idleFacingYawRadians;
    private Vector2 _landingCarryVelocityXZ;
    private float _landingCushionDurationSeconds;
    private float _landingCushionRemainingSeconds;
    private bool _isMovingLanding;
    private float _movingLandingNormalizedSpeed;
    private float _initialFacingYawRadians;

    // Double-buffered playback trajectories: main thread reads _activeBuffer;
    // worker writes into _stagingBuffer and publishes _stagedSequence under _swapSync.
    private readonly object _swapSync = new();
    private SomaTrajectoryPlaybackBuffer _activeBuffer = new();
    private SomaTrajectoryPlaybackBuffer _stagingBuffer = new();
    private long _stagedSequence;
    private long _stagedRequestTimestamp;
    private long _consumedSequence;

    // Latest-wins replan mailbox (zero per-replan allocation).
    private readonly object _requestSync = new();
    private readonly SomaPoseTransform[] _pendingContext =
        new SomaPoseTransform[SomaModelContract.ContextPoseCount * JointCount];
    private readonly SomaPoseTransform[] _pendingTargets =
        new SomaPoseTransform[SomaModelContract.TargetPoseCapacity * JointCount];
    private int _pendingTargetCount;
    private SomaInferenceConfig _pendingConfig = SomaInferenceConfig.Default;
    private int _pendingStyleIndex = SomaLocomotionPoseBank.StandingStyleIndex;
    private float _pendingFacingYawRadians;
    private long _pendingRequestSequence;
    private long _pendingRequestTimestamp;
    private bool _hasPendingRequest;

    // Physics-thread to animation-thread root reconciliation mailbox.
    private readonly object _reconciliationSync = new();
    private bool _hasPendingReconciliation;
    private Vector3 _pendingPhysicsFootPosition;
    private Vector3 _pendingActualPlanarVelocity;
    private Vector3 _pendingWallNormal;
    private bool _pendingIsBlocked;

    // Worker-owned scratch buffers (accessed strictly by the worker thread, or main thread in sync mode).
    private readonly SomaPoseTransform[] _workerContext =
        new SomaPoseTransform[SomaModelContract.ContextPoseCount * JointCount];
    private readonly SomaPoseTransform[] _workerTargets =
        new SomaPoseTransform[SomaModelContract.TargetPoseCapacity * JointCount];
    private readonly SomaPoseTransform[] _workerIdleFallbackPose = new SomaPoseTransform[JointCount];
    private readonly SomaPoseTransform[] _workerWorldPoseScratch = new SomaPoseTransform[JointCount];
    private readonly Quaternion[] _workerSmoothRotScratch =
        new Quaternion[SomaModelContract.MaxValidFrameCount * 21];
    private readonly SomaInferenceInputBuffer _workerInput = new();
    private readonly SomaInferenceOutputBuffer _workerOutput = new();
    private int _recoveryConditioningRemainingReplans;

    private readonly float[] _smoothedMuscles = new float[MotionBricksEvaluatedPose.MuscleChannelCount];
    private bool _hasSmoothedMuscles;
    private Quaternion _smoothedBodyRotation = Quaternion.Identity;
    private bool _hasSmoothedBodyRotation;

    private readonly AutoResetEvent? _workerSignal;
    private readonly Thread? _workerThread;
    private volatile bool _stopRequested;
    private bool _disposed;

    private float _avatarLegScale = 1.0f;
    private float _worldRootMotionScale = 1.0f;
    private bool _hasCanonicalOrigin;
    private Vector3 _canonicalBodyPosition;
    private Quaternion _canonicalBodyRotation = Quaternion.Identity;
    private long _nextSequence;
    private long _completedReplanCount;
    private long _contextSeedCount;
    private int _lastDecodedFrameCount;
    private double _lastInferenceMs;
    private volatile string _diagnosticStatus;

    public MotionBricksAsyncLocomotionDriver(
        IMotionBricksInferenceSession? inferenceSession,
        bool ownsSession = false,
        bool useBackgroundThread = true,
        SomaRestSkeletonCalibration? calibration = null,
        SomaSmartLocomotionSettings? settings = null,
        SomaLocomotionPoseBank? poseBank = null,
        float avatarLegScale = 1.0f)
        : this(
            inferenceSession,
            pendingSessionTask: null,
            ownsSession,
            useBackgroundThread,
            calibration,
            settings,
            poseBank,
            avatarLegScale)
    {
    }

    internal MotionBricksAsyncLocomotionDriver(
        Task<(IMotionBricksInferenceSession? Session, string? Error)> pendingSessionTask,
        SomaRestSkeletonCalibration? calibration = null,
        SomaSmartLocomotionSettings? settings = null,
        SomaLocomotionPoseBank? poseBank = null,
        float avatarLegScale = 1.0f)
        : this(
            inferenceSession: null,
            pendingSessionTask: pendingSessionTask ?? throw new ArgumentNullException(nameof(pendingSessionTask)),
            ownsSession: true,
            useBackgroundThread: true,
            calibration,
            settings,
            poseBank,
            avatarLegScale)
    {
    }

    private MotionBricksAsyncLocomotionDriver(
        IMotionBricksInferenceSession? inferenceSession,
        Task<(IMotionBricksInferenceSession? Session, string? Error)>? pendingSessionTask,
        bool ownsSession,
        bool useBackgroundThread,
        SomaRestSkeletonCalibration? calibration,
        SomaSmartLocomotionSettings? settings,
        SomaLocomotionPoseBank? poseBank,
        float avatarLegScale)
    {
        _inferenceSession = inferenceSession;
        _pendingSessionTask = pendingSessionTask;
        _deferredSessionPending = pendingSessionTask is not null;
        _ownsSession = ownsSession;
        _useBackgroundThread = useBackgroundThread;
        _calibration = calibration ?? SomaRestSkeletonCalibration.CanonicalDefault;
        _settings = settings ?? SomaSmartLocomotionSettings.Default;
        _poseBank = poseBank ?? new SomaLocomotionPoseBank(_calibration);
        _planner = new SomaSmartLocomotionPlanner(_settings, _poseBank);
        _converter = new SomaToHumanoidMuscleConverter(_calibration);
        _poseSampler = new HumanoidToSomaPoseSampler(_calibration);
        _avatarLegScale = float.IsFinite(avatarLegScale) && avatarLegScale > 0.01f ? avatarLegScale : 1.0f;
        _poseBank.CopyIdlePose(_workerIdleFallbackPose);

        SeedNeutralState();

        if (_deferredSessionPending)
        {
            _diagnosticStatus = "Warming up TensorRT SOMA engine on background worker.";
        }
        else if (_inferenceSession is null)
        {
            _diagnosticStatus = "No MotionBricks TensorRT inference session is bound.";
        }
        else if (!_inferenceSession.IsEngineReady)
        {
            _diagnosticStatus = "TensorRT session is bound, but the SOMA engine is not built or loaded.";
        }
        else
        {
            string targetSource = _poseBank.HasMocapTargets ? "SOMA mocap targets" : "procedural targets";
            _diagnosticStatus = _useBackgroundThread
                ? $"Ready (Async TensorRT worker active, {targetSource})."
                : $"Ready (Synchronous TensorRT mode, {targetSource}).";
        }

        if (_useBackgroundThread && (_deferredSessionPending || _inferenceSession is { IsEngineReady: true }))
        {
            _workerSignal = new AutoResetEvent(false);
            _workerThread = new Thread(WorkerLoop)
            {
                Name = "MotionBricks-Worker",
                IsBackground = true,
            };
            _workerThread.Start();
        }
    }

    public bool IsReady => !_disposed
        && !_deferredSessionFailed
        && (_deferredSessionPending || _inferenceSession is { IsEngineReady: true });
    public string DiagnosticStatus => _diagnosticStatus;
    public long CompletedReplanCount => Volatile.Read(ref _completedReplanCount);
    public long ContextSeedCount => Volatile.Read(ref _contextSeedCount);
    public int LastDecodedFrameCount => Volatile.Read(ref _lastDecodedFrameCount);
    public double LastInferenceMilliseconds => Volatile.Read(ref _lastInferenceMs);
    public int ActiveBufferHeadroomFrames => _activeBuffer.RemainingFrames;
    public SomaSmartLocomotionPlanner Planner => _planner;

    public float AvatarLegScale
    {
        get => _avatarLegScale;
        set => _avatarLegScale = float.IsFinite(value) && value > 0.01f ? value : 1.0f;
    }

    public void OnStateEntered()
        => _hasCanonicalOrigin = false;

    public void OnStateExited()
    {
    }

    public void SetInitialFacingYaw(float facingYawRadians)
    {
        if (!float.IsFinite(facingYawRadians))
            return;

        _initialFacingYawRadians = CriticalSpringDamper.WrapAngleRadians(facingYawRadians);
        ResetPlayback();
    }

    public void SetTrajectoryProber(IMotionBricksTrajectoryProber? prober)
        => _trajectoryProber = prober;

    public void SetWorldRootMotionScale(float worldRootMotionScale)
        => _worldRootMotionScale = float.IsFinite(worldRootMotionScale) && worldRootMotionScale > 0.01f
            ? worldRootMotionScale
            : 1.0f;

    private float EffectiveWorldScale => _avatarLegScale * _worldRootMotionScale;

    public void ResetPlayback()
    {
        lock (_requestSync)
            _hasPendingRequest = false;

        lock (_swapSync)
        {
            _activeBuffer.Clear();
            _stagingBuffer.Clear();
            _stagedSequence = 0L;
            _stagedRequestTimestamp = 0L;
            _consumedSequence = 0L;
        }

        _hasCanonicalOrigin = false;
        _hasLiveWorldFootPosition = false;
        _liveActualPlanarVelocity = Vector3.Zero;
        _hasSomaWorldOriginOffset = false;
        _somaWorldOriginOffsetXZ = Vector3.Zero;
        _trajectoryPreviewAccumulator = 0.0f;
        _wasGrounded = true;
        _wasProne = false;
        _proneTransitionProgress = 0.0f;
        _proneTransitionUseStandingEndpoint = true;
        _lastNonProneStyleIndex = SomaLocomotionPoseBank.StandingStyleIndex;
        _proneCrawlPhaseRadians = 0.0f;
        _isInBreathingIdle = false;
        _isCrossfadingToIdle = false;
        _idleSettledSeconds = 0.0f;
        _idleCrossfadeElapsedSeconds = 0.0f;
        _idleBreathingTimeSeconds = 0.0f;
        _idleStyleIndex = SomaLocomotionPoseBank.StandingStyleIndex;
        _activeIdleClip = null;
        _hasQueuedStoppingReplan = false;
        _stoppingReplanSequence = 0L;
        _idleAnchorRootXZ = Vector3.Zero;
        _idleFacingYawRadians = _initialFacingYawRadians;
        _landingCarryVelocityXZ = Vector2.Zero;
        _landingCushionDurationSeconds = 0.0f;
        _landingCushionRemainingSeconds = 0.0f;
        _isMovingLanding = false;
        _movingLandingNormalizedSpeed = 0.0f;
        _hasSmoothedMuscles = false;
        _hasSmoothedBodyRotation = false;
        _ungroundedDurationSeconds = 0.0f;
        Volatile.Write(ref _recoveryConditioningRemainingReplans, 0);
        _planner.Reset(_initialFacingYawRadians);
        SeedNeutralState();
    }

    /// <summary>
    /// Converts four 30 Hz live humanoid pose history frames into SOMA context poses without
    /// heap allocation and seeds the driver before its first replan.
    /// </summary>
    public void SeedContextFromHumanoidHistory(ReadOnlySpan<HumanoidPoseHistoryFrame> fourContextFrames)
    {
        if (fourContextFrames.Length < SomaModelContract.ContextPoseCount)
            return;

        _poseSampler.SampleFourContextPoses(fourContextFrames, _avatarLegScale, _seedSomaPoses);
        Quaternion newestRootRot = _seedSomaPoses[(SomaModelContract.ContextPoseCount - 1) * JointCount].Rotation;
        float facingYaw = ExtractFacingYaw(newestRootRot, _initialFacingYawRadians);
        SeedContextPoses(_seedSomaPoses, facingYaw);
    }

    /// <summary>
    /// Records Jolt physics capsule position and wall-blocking feedback from the physics thread
    /// so the animation thread applies reconciliation safely without data races or free-walk jitter.
    /// </summary>
    public void ApplyPhysicsRootReconciliation(
        Vector3 actualFootPosition,
        Vector3 actualPlanarVelocity,
        Vector3 wallNormal,
        bool isBlocked)
    {
        lock (_reconciliationSync)
        {
            _pendingPhysicsFootPosition = actualFootPosition;
            _pendingActualPlanarVelocity = actualPlanarVelocity;
            _pendingWallNormal = wallNormal;
            _pendingIsBlocked = _pendingIsBlocked || isBlocked;
            _hasPendingReconciliation = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stopRequested = true;
        _workerSignal?.Set();
        if (_workerThread is not null && _workerThread.IsAlive)
            _workerThread.Join(millisecondsTimeout: 2000);

        _workerSignal?.Dispose();
        if (_ownsSession)
            _inferenceSession?.Dispose();
    }

    private void SeedContextPoses(ReadOnlySpan<SomaPoseTransform> fourContextPoses, float facingYawRadians)
    {
        _history.SeedFromFourPoses(fourContextPoses);
        ReadOnlySpan<SomaPoseTransform> newest = fourContextPoses.Slice(
            (SomaModelContract.ContextPoseCount - 1) * JointCount,
            JointCount);
        newest.CopyTo(_displayedPose);
        _hasDisplayedPose = true;
        _isInBreathingIdle = false;
        _isCrossfadingToIdle = false;
        _idleSettledSeconds = 0.0f;
        _idleCrossfadeElapsedSeconds = 0.0f;
        _hasQueuedStoppingReplan = false;
        _stoppingReplanSequence = 0L;
        _hasSmoothedMuscles = false;
        _hasSmoothedBodyRotation = false;
        _ungroundedDurationSeconds = 0.0f;
        _idleFacingYawRadians = facingYawRadians;
        _planner.Reset(facingYawRadians);
        Interlocked.Increment(ref _contextSeedCount);
    }

    private void ApplyPendingPhysicsReconciliationOnAnimationThread()
    {
        Vector3 actualFootPosition;
        Vector3 actualPlanarVelocity;
        Vector3 wallNormal;
        bool isBlocked;

        lock (_reconciliationSync)
        {
            if (!_hasPendingReconciliation)
                return;

            actualFootPosition = _pendingPhysicsFootPosition;
            actualPlanarVelocity = _pendingActualPlanarVelocity;
            wallNormal = _pendingWallNormal;
            isBlocked = _pendingIsBlocked;
            _hasPendingReconciliation = false;
            _pendingIsBlocked = false;
        }

        _liveWorldFootPosition = actualFootPosition;
        _liveActualPlanarVelocity = actualPlanarVelocity;
        _hasLiveWorldFootPosition = true;

        if (_hasDisplayedPose)
        {
            float worldPerSoma = EffectiveWorldScale;
            Vector3 currentRoot = _displayedPose[0].Position;
            Vector3 currentRootXZ = new(currentRoot.X, 0.0f, currentRoot.Z);
            Vector3 actualFootXZ = new(actualFootPosition.X, 0.0f, actualFootPosition.Z);

            if (!_hasSomaWorldOriginOffset)
            {
                _somaWorldOriginOffsetXZ = actualFootXZ - currentRootXZ * worldPerSoma;
                _hasSomaWorldOriginOffset = true;
            }

            Vector3 expectedWorldXZ = _somaWorldOriginOffsetXZ + currentRootXZ * worldPerSoma;
            Vector3 worldDriftXZ = actualFootXZ - expectedWorldXZ;
            float driftSq = worldDriftXZ.LengthSquared();

            // Snap immediately on real wall collisions, or when open-space world drift exceeds
            // normal render-to-physics pipelining lag (0.40 m).
            if ((isBlocked && driftSq > 1.0e-6f) || driftSq > 0.16f)
            {
                Vector3 somaCorrection = worldDriftXZ / worldPerSoma;
                _displayedPose[0] = new SomaPoseTransform(currentRoot + somaCorrection, _displayedPose[0].Rotation);
                _history.ApplyRootTranslationOffset(somaCorrection);
                _activeBuffer.ApplyRootTranslationOffset(somaCorrection);

                // Shift the canonical origin by the same SOMA-space correction so physics reconciliation
                // does not inject a phantom root-motion delta back into Jolt on the next frame.
                if (_hasCanonicalOrigin)
                    _canonicalBodyPosition += somaCorrection * _avatarLegScale;
                if (_isInBreathingIdle)
                    _idleAnchorRootXZ += somaCorrection;
            }
        }

        _planner.NotifyPhysicsCollisionClamp(actualPlanarVelocity, wallNormal, isBlocked);
    }

    private void SeedNeutralState()
    {
        _poseBank.CopyIdlePoseAtYaw(_initialFacingYawRadians, _displayedPose);
        _displayedFootContacts = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        _hasDisplayedPose = true;
        _history.SeedFromPose(_displayedPose);
    }
}

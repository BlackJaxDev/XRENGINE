using System.Diagnostics;
using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Production <see cref="IMotionBricksLocomotionDriver"/> that runs Smart Locomotion
/// planning on the animation thread and dispatches TensorRT SOMA replans on a dedicated
/// background worker thread with lock-free/minimal-lock double-buffered trajectory swaps
/// and smooth 50 ms splice cross-blending.
/// </summary>
public sealed class MotionBricksAsyncLocomotionDriver : IMotionBricksLocomotionDriver
{
    private const int JointCount = SomaModelContract.JointCount;

    private readonly IMotionBricksInferenceSession? _inferenceSession;
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
    private Vector4 _displayedFootContacts;
    private bool _hasDisplayedPose;

    // Double-buffered playback trajectories: main thread reads _activeBuffer;
    // worker writes into _stagingBuffer and publishes _stagedSequence under _swapSync.
    private readonly object _swapSync = new();
    private SomaTrajectoryPlaybackBuffer _activeBuffer = new();
    private SomaTrajectoryPlaybackBuffer _stagingBuffer = new();
    private long _stagedSequence;
    private long _consumedSequence;

    // Latest-wins replan mailbox (zero per-replan allocation).
    private readonly object _requestSync = new();
    private readonly SomaPoseTransform[] _pendingContext =
        new SomaPoseTransform[SomaModelContract.ContextPoseCount * JointCount];
    private readonly SomaPoseTransform[] _pendingTargets =
        new SomaPoseTransform[SomaModelContract.TargetPoseCapacity * JointCount];
    private int _pendingTargetCount;
    private SomaInferenceConfig _pendingConfig = SomaInferenceConfig.Default;
    private long _pendingRequestSequence;
    private bool _hasPendingRequest;

    // Worker-owned scratch buffers (accessed strictly by the worker thread, or main thread in sync mode).
    private readonly SomaPoseTransform[] _workerContext =
        new SomaPoseTransform[SomaModelContract.ContextPoseCount * JointCount];
    private readonly SomaPoseTransform[] _workerTargets =
        new SomaPoseTransform[SomaModelContract.TargetPoseCapacity * JointCount];
    private readonly SomaInferenceInputBuffer _workerInput = new();
    private readonly SomaInferenceOutputBuffer _workerOutput = new();

    private readonly AutoResetEvent? _workerSignal;
    private readonly Thread? _workerThread;
    private volatile bool _stopRequested;
    private bool _disposed;

    private float _avatarLegScale = 1.0f;
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
    {
        _inferenceSession = inferenceSession;
        _ownsSession = ownsSession;
        _useBackgroundThread = useBackgroundThread;
        _calibration = calibration ?? SomaRestSkeletonCalibration.CanonicalDefault;
        _settings = settings ?? SomaSmartLocomotionSettings.Default;
        _poseBank = poseBank ?? new SomaLocomotionPoseBank(_calibration);
        _planner = new SomaSmartLocomotionPlanner(_settings, _poseBank);
        _converter = new SomaToHumanoidMuscleConverter(_calibration);
        _poseSampler = new HumanoidToSomaPoseSampler(_calibration);
        _avatarLegScale = float.IsFinite(avatarLegScale) && avatarLegScale > 0.01f ? avatarLegScale : 1.0f;

        SeedNeutralState();

        if (_inferenceSession is null)
        {
            _diagnosticStatus = "No MotionBricks TensorRT inference session is bound.";
        }
        else if (!_inferenceSession.IsEngineReady)
        {
            _diagnosticStatus = "TensorRT session is bound, but the SOMA engine is not built or loaded.";
        }
        else
        {
            _diagnosticStatus = _useBackgroundThread
                ? "Ready (Async TensorRT worker active)."
                : "Ready (Synchronous TensorRT mode).";
        }

        if (_useBackgroundThread && _inferenceSession is { IsEngineReady: true })
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

    public bool IsReady => !_disposed && _inferenceSession is { IsEngineReady: true };
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

    public void ResetPlayback()
    {
        lock (_requestSync)
            _hasPendingRequest = false;

        lock (_swapSync)
        {
            _activeBuffer.Clear();
            _stagingBuffer.Clear();
            _stagedSequence = 0L;
            _consumedSequence = 0L;
        }

        _hasCanonicalOrigin = false;
        _planner.Reset();
        SeedNeutralState();
    }

    /// <summary>
    /// Seeds the 4-frame 30 Hz context history from live avatar poses (used when entering
    /// MotionBricks locomotion from a traditional animation clip state).
    /// </summary>
    public void SeedContextPoses(ReadOnlySpan<SomaPoseTransform> fourContextPoses, float facingYawRadians)
    {
        _history.SeedFromFourPoses(fourContextPoses);
        ReadOnlySpan<SomaPoseTransform> newest = fourContextPoses.Slice(
            (SomaModelContract.ContextPoseCount - 1) * JointCount,
            JointCount);
        newest.CopyTo(_displayedPose);
        _hasDisplayedPose = true;
        _planner.Reset(facingYawRadians);
        Interlocked.Increment(ref _contextSeedCount);
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
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, newestRootRot);
        float lenSq = forward.X * forward.X + forward.Z * forward.Z;
        float facingYaw = lenSq > 1.0e-8f ? MathF.Atan2(forward.X, forward.Z) : 0.0f;
        SeedContextPoses(_seedSomaPoses, facingYaw);
    }

    private readonly object _reconciliationSync = new();
    private bool _hasPendingReconciliation;
    private Vector3 _pendingPhysicsFootPosition;
    private Vector3 _pendingActualPlanarVelocity;
    private Vector3 _pendingWallNormal;
    private bool _pendingIsBlocked;

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

    private void ApplyPendingPhysicsReconciliationOnAnimationThread()
    {
        bool hasPending;
        Vector3 actualFootPosition;
        Vector3 actualPlanarVelocity;
        Vector3 wallNormal;
        bool isBlocked;

        lock (_reconciliationSync)
        {
            hasPending = _hasPendingReconciliation;
            if (!hasPending)
                return;

            actualFootPosition = _pendingPhysicsFootPosition;
            actualPlanarVelocity = _pendingActualPlanarVelocity;
            wallNormal = _pendingWallNormal;
            isBlocked = _pendingIsBlocked;
            _hasPendingReconciliation = false;
            _pendingIsBlocked = false;
        }

        if (_hasDisplayedPose)
        {
            Vector3 currentRoot = _displayedPose[0].Position;
            var planarCorrection = new Vector3(
                actualFootPosition.X - currentRoot.X,
                0.0f,
                actualFootPosition.Z - currentRoot.Z);
            float driftSq = planarCorrection.LengthSquared();
            // Snap the trajectory root immediately on wall collisions, or when drift exceeds the
            // normal 1-step render-to-physics phase offset (0.20 m).
            if ((isBlocked && driftSq > 1.0e-6f) || driftSq > 0.04f)
            {
                _displayedPose[0] = new SomaPoseTransform(currentRoot + planarCorrection, _displayedPose[0].Rotation);
                _history.ApplyRootTranslationOffset(planarCorrection);
                _activeBuffer.ApplyRootTranslationOffset(planarCorrection);
            }
        }

        _planner.NotifyPhysicsCollisionClamp(actualPlanarVelocity, wallNormal, isBlocked);
    }

    public void AdvanceAndEvaluate(in MotionBricksSteeringInput input, MotionBricksEvaluatedPose destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_disposed || !IsReady)
        {
            destination.HasValidPose = false;
            return;
        }

        ApplyPendingPhysicsReconciliationOnAnimationThread();
        _planner.UpdateSteering(input);

        // Adopt any completed trajectory produced by the background worker.
        TrySwapCompletedTrajectory();

        if (_planner.ShouldTriggerReplan(input, _activeBuffer.RemainingFrames, out bool isUrgent))
            EnqueueReplan(input, isUrgent);

        // In synchronous mode, execute immediately and swap.
        if (!_useBackgroundThread)
        {
            ProcessPendingRequestCore();
            TrySwapCompletedTrajectory();
        }

        if (_activeBuffer.TryAdvanceAndSample(input.DeltaSeconds, _displayedPose, out Vector4 contacts))
        {
            _displayedFootContacts = contacts;
            _hasDisplayedPose = true;
            _history.RecordPose(_displayedPose, input.DeltaSeconds);

            if (_activeBuffer.IsExhausted)
                _diagnosticStatus = "Warning: Trajectory buffer exhausted while waiting for TensorRT replan; holding last valid pose.";
        }
        else if (!_hasDisplayedPose)
        {
            destination.HasValidPose = false;
            return;
        }

        Array.Clear(destination.MuscleValues);
        _converter.ConvertFrame(
            _displayedPose,
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
        destination.FootContacts = _displayedFootContacts;
        destination.HasValidPose = true;
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

    private void SeedNeutralState()
    {
        _poseBank.CopyIdlePose(_displayedPose);
        _displayedFootContacts = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        _hasDisplayedPose = true;
        _history.SeedFromPose(_displayedPose);
    }

    private void EnqueueReplan(in MotionBricksSteeringInput input, bool isUrgent)
    {
        lock (_requestSync)
        {
            if (!_history.TrySampleContextToken(_pendingContext))
                return;

            Vector3 currentRoot = _displayedPose[0].Position;
            _pendingConfig = _planner.BuildTargetPoses(
                input,
                currentRoot,
                isUrgent,
                _pendingTargets,
                out _pendingTargetCount);
            _pendingRequestSequence = ++_nextSequence;
            _hasPendingRequest = true;
        }

        _workerSignal?.Set();
    }

    private void TrySwapCompletedTrajectory()
    {
        lock (_swapSync)
        {
            if (_stagedSequence <= _consumedSequence || _stagingBuffer.FrameCount <= 0)
                return;

            (_activeBuffer, _stagingBuffer) = (_stagingBuffer, _activeBuffer);
            _consumedSequence = _stagedSequence;
            if (_hasDisplayedPose)
            {
                _activeBuffer.BeginSpliceFrom(
                    _displayedPose,
                    _displayedFootContacts,
                    _settings.SpliceBlendDurationSeconds);
            }
        }
    }

    private void WorkerLoop()
    {
        while (!_stopRequested)
        {
            _workerSignal!.WaitOne(millisecondsTimeout: 50);
            if (_stopRequested)
                break;

            ProcessPendingRequestCore();
        }
    }

    private void ProcessPendingRequestCore()
    {
        if (_inferenceSession is null || !_inferenceSession.IsEngineReady)
            return;

        int targetCount;
        SomaInferenceConfig config;
        long requestSequence;

        lock (_requestSync)
        {
            if (!_hasPendingRequest)
                return;

            _pendingContext.CopyTo(_workerContext, 0);
            _pendingTargets.CopyTo(_workerTargets, 0);
            targetCount = _pendingTargetCount;
            config = _pendingConfig;
            requestSequence = _pendingRequestSequence;
            _hasPendingRequest = false;
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        SomaTensorCodec.EncodeInputs(
            _workerContext,
            _workerTargets,
            targetCount,
            config,
            SomaPoseTransform.Identity,
            _workerInput);

        if (!_inferenceSession.TryRunSomaInference(_workerInput, _workerOutput, out string? error))
        {
            _diagnosticStatus = error ?? "TensorRT SOMA inference failed during async replan.";
            return;
        }

        int validFrames = SomaTensorCodec.DecodeOutputs(
            _workerOutput,
            _workerInput.GuideAlignment,
            SomaPoseTransform.Identity,
            _calibration.ReferenceLocalPoses);
        if (validFrames <= 0)
        {
            _diagnosticStatus = "SOMA async replan decoded zero valid frames.";
            return;
        }

        if (config.CorrectGeneratedDiff)
        {
            ReadOnlySpan<SomaPoseTransform> lastContext = _workerContext.AsSpan(
                (SomaModelContract.ContextPoseCount - 1) * JointCount,
                JointCount);
            ReadOnlySpan<SomaPoseTransform> firstTarget = _workerTargets.AsSpan(0, JointCount);
            SomaTensorCodec.ApplyEndpointCorrection(lastContext, firstTarget, _workerOutput);
        }

        double elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        Volatile.Write(ref _lastInferenceMs, elapsedMs);

        lock (_swapSync)
        {
            _stagingBuffer.CopyFromDecodedOutput(_workerOutput, requestSequence);
            _stagedSequence = requestSequence;
        }

        Volatile.Write(ref _lastDecodedFrameCount, validFrames);
        Interlocked.Increment(ref _completedReplanCount);
        _diagnosticStatus = _useBackgroundThread
            ? "Ready (Async TensorRT worker active)."
            : "Ready (Synchronous TensorRT mode).";
    }
}

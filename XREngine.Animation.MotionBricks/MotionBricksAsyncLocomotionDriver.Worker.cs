using System.Diagnostics;
using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation.MotionBricks;

public sealed partial class MotionBricksAsyncLocomotionDriver
{
    private long EnqueueReplan(in MotionBricksSteeringInput input, bool isUrgent)
    {
        long enqueuedSequence;
        lock (_requestSync)
        {
            if (!_history.TrySampleContextToken(_pendingContext))
                return 0L;

            Vector3 currentRoot = _displayedPose[0].Position;
            Vector3 footOrigin = ResolveWorldFootOrigin();
            bool forceConditionTargetLocalPoses = false;
            int remainingRecovery = Volatile.Read(ref _recoveryConditioningRemainingReplans);
            if (remainingRecovery > 0)
            {
                Volatile.Write(ref _recoveryConditioningRemainingReplans, remainingRecovery - 1);
                forceConditionTargetLocalPoses = true;
            }

            _pendingConfig = _planner.BuildTargetPoses(
                input,
                currentRoot,
                footOrigin,
                EffectiveWorldScale,
                _trajectoryProber,
                isUrgent,
                _pendingTargets,
                out _pendingTargetCount,
                forceConditionTargetLocalPoses);
            _pendingStyleIndex = input.StyleIndex;
            _pendingFacingYawRadians = _planner.CurrentFacingYawRadians;
            _trajectoryPreviewAccumulator = 0.0f;
            enqueuedSequence = ++_nextSequence;
            _pendingRequestSequence = enqueuedSequence;
            _pendingRequestTimestamp = Stopwatch.GetTimestamp();
            _hasPendingRequest = true;
        }

        _workerSignal?.Set();
        return enqueuedSequence;
    }

    private long EnqueueIdleClipTransitionReplan(
        in MotionBricksSteeringInput input,
        SomaMotionClip idleClip,
        bool isStanceChange = false)
    {
        long enqueuedSequence;
        lock (_requestSync)
        {
            if (!_history.TrySampleContextToken(_pendingContext))
                return 0L;

            Vector3 currentRoot = _displayedPose[0].Position;
            Vector2 residualVel = isStanceChange ? Vector2.Zero : _planner.DesiredPlanarVelocity;
            Vector3 stopAnchorXZ = new(
                currentRoot.X + residualVel.X * 0.25f,
                0.0f,
                currentRoot.Z + residualVel.Y * 0.25f);
            float facingYaw = _planner.CurrentFacingYawRadians;

            // Keep planner state synced to stopped so it does not re-queue extra stopping replans.
            Vector3 footOrigin = ResolveWorldFootOrigin();
            _planner.BuildTargetPoses(
                input,
                currentRoot,
                footOrigin,
                EffectiveWorldScale,
                _trajectoryProber,
                isUrgentTurnOrStop: true,
                _pendingTargets,
                out _);

            idleClip.CopyPlacedFourPoseToken(0.0f, stopAnchorXZ, facingYaw, _pendingTargets);
            _pendingTargetCount = SomaModelContract.TargetPoseCapacity;
            int allowedBits = isStanceChange
                ? SomaInferenceConfig.CreateAllowedTokenBits(6, 7)
                : SomaInferenceConfig.CreateAllowedTokenBits(8, 14);
            _pendingConfig = new SomaInferenceConfig(
                allowedBits,
                correctGeneratedDiff: true,
                headingAlignment: new Vector2(1.0f, 0.0f),
                conditionTargetLocalPoses: true);
            _pendingStyleIndex = input.StyleIndex;
            _pendingFacingYawRadians = facingYaw;
            _trajectoryPreviewAccumulator = 0.0f;
            enqueuedSequence = ++_nextSequence;
            _pendingRequestSequence = enqueuedSequence;
            _pendingRequestTimestamp = Stopwatch.GetTimestamp();
            _hasPendingRequest = true;
        }

        _workerSignal?.Set();
        return enqueuedSequence;
    }

    private void WorkerLoop()
    {
        if (_pendingSessionTask is { } sessionTask)
        {
            try
            {
                (IMotionBricksInferenceSession? loadedSession, string? loadError) = sessionTask.GetAwaiter().GetResult();
                if (_stopRequested || _disposed)
                {
                    loadedSession?.Dispose();
                    return;
                }

                if (loadedSession is { IsEngineReady: true })
                {
                    _inferenceSession = loadedSession;
                    _diagnosticStatus = "Ready (Async TensorRT worker active).";
                }
                else
                {
                    loadedSession?.Dispose();
                    _deferredSessionFailed = true;
                    _diagnosticStatus = loadError ?? "TensorRT session warmup failed to produce a ready SOMA engine.";
                }
            }
            catch (Exception ex)
            {
                _deferredSessionFailed = true;
                _diagnosticStatus = $"TensorRT session warmup failed: {ex.Message}";
            }
            finally
            {
                _pendingSessionTask = null;
                _deferredSessionPending = false;
            }

            if (_deferredSessionFailed)
                return;
        }

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
        int styleIndex;
        float fallbackYawRadians;
        long requestSequence;
        long requestTimestamp;

        lock (_requestSync)
        {
            if (!_hasPendingRequest)
                return;

            _pendingContext.CopyTo(_workerContext, 0);
            _pendingTargets.CopyTo(_workerTargets, 0);
            targetCount = _pendingTargetCount;
            config = _pendingConfig;
            styleIndex = _pendingStyleIndex;
            fallbackYawRadians = _pendingFacingYawRadians;
            requestSequence = _pendingRequestSequence;
            requestTimestamp = _pendingRequestTimestamp;
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
            // Smooth only the start seam across 4 frames and keep endRootWeightScale at 0 so 100%
            // of planar travel comes from SOMA's own foot-locked root trajectory without sliding.
            SomaTensorCodec.ApplyStartAndRootEndpointCorrection(
                lastContext,
                firstTarget,
                _workerOutput,
                decayFrames: 4,
                endRootWeightScale: 0.0f);
        }

        if (styleIndex == SomaLocomotionPoseBank.StandingStyleIndex)
            SanitizeStandingDecodedOutput(_workerOutput, validFrames, fallbackYawRadians);

        SmoothDecodedUpperBodyRotations(_workerOutput, validFrames);

        double elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        Volatile.Write(ref _lastInferenceMs, elapsedMs);

        lock (_swapSync)
        {
            _stagingBuffer.CopyFromDecodedOutput(_workerOutput, requestSequence);
            _stagedSequence = requestSequence;
            _stagedRequestTimestamp = requestTimestamp;
        }

        Volatile.Write(ref _lastDecodedFrameCount, validFrames);
        Interlocked.Increment(ref _completedReplanCount);
        _diagnosticStatus = _useBackgroundThread
            ? "Ready (Async TensorRT worker active)."
            : "Ready (Synchronous TensorRT mode).";
    }
}

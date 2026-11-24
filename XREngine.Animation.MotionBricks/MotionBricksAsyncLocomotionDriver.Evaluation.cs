using System.Diagnostics;
using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation.MotionBricks;

public sealed partial class MotionBricksAsyncLocomotionDriver
{
    public bool TryCopyDebugSnapshot(MotionBricksDebugSnapshot destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();

        lock (_debugSync)
        {
            if (!_debugHasCurrentPose)
                return false;

            int dotCount = Math.Min(_debugTrajectoryCount, destination.TrajectoryPoints.Length);
            if (dotCount > 0)
                _debugTrajectorySnapshot.AsSpan(0, dotCount).CopyTo(destination.TrajectoryPoints);
            destination.TrajectoryPointCount = dotCount;

            Vector3 anchor = _debugWorldFootAnchor;
            Vector3 currentSomaRootXZ = new(_debugDisplayedPoseSnapshot[0].Position.X, 0.0f, _debugDisplayedPoseSnapshot[0].Position.Z);
            float legScale = EffectiveWorldScale;

            ComposeWorldSkeletonJoints(
                _debugDisplayedPoseSnapshot,
                currentSomaRootXZ,
                anchor,
                legScale,
                destination.CurrentSkeletonJoints);
            destination.CurrentFootContacts = _debugFootContactsSnapshot;
            destination.HasCurrentSkeleton = true;

            if (_debugHasFutureMidPose)
            {
                ComposeWorldSkeletonJoints(
                    _debugFutureMidPoseSnapshot,
                    currentSomaRootXZ,
                    anchor,
                    legScale,
                    destination.FutureMidSkeletonJoints);
                destination.FutureMidLeadSeconds = _debugFutureMidLeadSeconds;
                destination.HasFutureMidSkeleton = true;
            }

            if (_debugHasFutureEndPose)
            {
                ComposeWorldSkeletonJoints(
                    _debugFutureEndPoseSnapshot,
                    currentSomaRootXZ,
                    anchor,
                    legScale,
                    destination.FutureEndSkeletonJoints);
                destination.FutureEndLeadSeconds = _debugFutureEndLeadSeconds;
                destination.HasFutureEndSkeleton = true;
            }

            destination.CurrentRootGroundPosition = anchor;
            destination.TargetRootGroundPosition = _debugTargetGroundWorld;
            destination.AvatarLegScale = legScale;
            destination.IsValid = true;
            return true;
        }
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

        float dt = float.IsFinite(input.DeltaSeconds) && input.DeltaSeconds > 0.0f
            ? input.DeltaSeconds
            : 0.0f;

        if (_inferenceSession is not { IsEngineReady: true })
        {
            _ungroundedDurationSeconds = 0.0f;
            AdvanceWarmupIdle(in input, dt);
        }
        else if (ShouldEvaluateAirborne(in input, dt))
        {
            AdvanceAirborne(in input, dt);
        }
        else if (input.StyleIndex == SomaLocomotionPoseBank.ProneStyleIndex || _proneTransitionProgress > 0.0f)
        {
            AdvanceProneOrTransition(in input, dt);
        }
        else if (!AdvanceGroundedLocomotion(in input, dt))
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

        if (input.IsGrounded
            && input.StyleIndex == SomaLocomotionPoseBank.StandingStyleIndex
            && !_isInBreathingIdle)
        {
            float crossfadeRemain = _isCrossfadingToIdle
                ? 1.0f - Math.Clamp(_idleCrossfadeElapsedSeconds / IdleCrossfadeDurationSeconds, 0.0f, 1.0f)
                : 1.0f;
            ApplyFeminineStylePostProcess(destination.MuscleValues, input, crossfadeRemain);
        }

        ApplyMuscleTemporalSmoothing(destination.MuscleValues, dt);
        bodyRotation = SmoothBodyRotation(bodyRotation, dt);

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

        // Refresh the ground trajectory preview periodically between replans and publish a
        // zero-allocation debug snapshot for trajectory dot and current/future skeleton rendering.
        _trajectoryPreviewAccumulator += dt;
        if (_trajectoryPreviewAccumulator >= 0.05f || _planner.ProjectedTrajectoryCount == 0)
        {
            _trajectoryPreviewAccumulator = 0.0f;
            Vector3 footOrigin = ResolveWorldFootOrigin();
            _planner.ProjectFutureTrajectory(input, footOrigin, EffectiveWorldScale, _trajectoryProber);
        }

        PublishDebugSnapshot();
    }

    private bool ShouldEvaluateAirborne(in MotionBricksSteeringInput input, float dt)
    {
        if (input.IsGrounded)
        {
            _ungroundedDurationSeconds = 0.0f;
            return false;
        }

        // Enter airborne immediately on upward jump takeoff or when already airborne; debounce brief
        // downward landing contact bounces so Jolt does not re-trigger landing resets.
        if (!_wasGrounded || input.VerticalVelocity >= JumpTakeoffVelocityThreshold)
        {
            _ungroundedDurationSeconds = AirborneConfirmDelaySeconds;
            return true;
        }

        _ungroundedDurationSeconds += dt;
        return _ungroundedDurationSeconds >= AirborneConfirmDelaySeconds;
    }

    private Quaternion SmoothBodyRotation(Quaternion targetRotation, float dt)
    {
        Quaternion normalized = SomaPoseTransform.NormalizeOrIdentity(targetRotation);
        if (!_hasSmoothedBodyRotation || dt <= 0.0f)
        {
            _smoothedBodyRotation = normalized;
            _hasSmoothedBodyRotation = true;
            return normalized;
        }

        float alpha = 1.0f - MathF.Exp(-BodyRotationSmoothingRate * dt);
        Quaternion q = Quaternion.Dot(_smoothedBodyRotation, normalized) < 0.0f
            ? new Quaternion(-normalized.X, -normalized.Y, -normalized.Z, -normalized.W)
            : normalized;
        _smoothedBodyRotation = SomaPoseTransform.NormalizeOrIdentity(
            Quaternion.Slerp(_smoothedBodyRotation, q, alpha));
        return _smoothedBodyRotation;
    }

    private void AdvanceWarmupIdle(in MotionBricksSteeringInput input, float dt)
    {
        // While TensorRT deserializes on the background worker, play a real mocap idle clip
        // (or fallback breathing idle) and record 30 Hz context so SOMA starts seamlessly.
        _idleBreathingTimeSeconds += dt;
        Vector3 anchorRootXZ = _hasDisplayedPose
            ? new Vector3(_displayedPose[0].Position.X, 0.0f, _displayedPose[0].Position.Z)
            : Vector3.Zero;
        SampleActiveIdlePose(
            _idleBreathingTimeSeconds,
            input.StyleIndex,
            anchorRootXZ,
            _planner.CurrentFacingYawRadians,
            breathingAmplitudeScale: 1.0f,
            _displayedPose);
        _displayedFootContacts = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        _hasDisplayedPose = true;
        _history.RecordPose(_displayedPose, dt);
    }

    private void AdvanceAirborne(in MotionBricksSteeringInput input, float dt)
    {
        // While airborne, drive the staged NVIDIA SOMA jump or fall pose from vertical velocity
        // and advance root XZ along carried planar momentum so the 30 Hz context history
        // preserves full running or walking velocity across touchdown into the landing replan.
        _wasGrounded = false;
        _wasProne = false;
        _proneTransitionProgress = 0.0f;
        _isInBreathingIdle = false;
        _isCrossfadingToIdle = false;
        _idleSettledSeconds = 0.0f;
        _idleCrossfadeElapsedSeconds = 0.0f;
        _hasQueuedStoppingReplan = false;
        _stoppingReplanSequence = 0L;
        _landingCushionRemainingSeconds = 0.0f;
        _landingCushionDurationSeconds = 0.0f;
        _isMovingLanding = false;

        Vector2 worldAirVelXZ = ResolveAirbornePlanarVelocityWorld();
        float invWorldScale = 1.0f / EffectiveWorldScale;
        Vector2 somaAirVelXZ = worldAirVelXZ * invWorldScale;
        _landingCarryVelocityXZ = somaAirVelXZ;

        Vector3 currentRoot = _hasDisplayedPose ? _displayedPose[0].Position : Vector3.Zero;
        if (dt > 0.0f)
            currentRoot += new Vector3(somaAirVelXZ.X * dt, 0.0f, somaAirVelXZ.Y * dt);

        _poseBank.BuildAirbornePose(input.VerticalVelocity, currentRoot, _planner.CurrentFacingYawRadians, _airborneScratch);
        BlendDisplayedPosePreservingRootXZ(_airborneScratch, blendRatePerSecond: 16.0f, dt);
        _displayedFootContacts = Vector4.Zero;
        _hasDisplayedPose = true;
        _history.RecordPose(_displayedPose, dt);
    }

    private void AdvanceProneOrTransition(in MotionBricksSteeringInput input, float dt)
    {
        // Route Stand/Crouch <-> Prone through grounded Crouch and Hands-and-Knees stages
        // and emit 100% of planar crawl velocity directly to Jolt root motion when prone.
        bool targetProne = input.StyleIndex == SomaLocomotionPoseBank.ProneStyleIndex;
        if (targetProne && !_wasProne)
        {
            _proneTransitionUseStandingEndpoint = _lastNonProneStyleIndex != SomaLocomotionPoseBank.CrouchedStyleIndex;
            _hasLastProneFacingYaw = false;
            lock (_swapSync)
            {
                _activeBuffer.Clear();
                _stagingBuffer.Clear();
            }
        }
        else if (!targetProne && _wasProne)
        {
            _proneTransitionUseStandingEndpoint = input.StyleIndex != SomaLocomotionPoseBank.CrouchedStyleIndex;
            lock (_swapSync)
            {
                _activeBuffer.Clear();
                _stagingBuffer.Clear();
            }
        }

        _wasGrounded = true;
        _wasProne = targetProne;
        _isInBreathingIdle = false;
        _isCrossfadingToIdle = false;
        _idleSettledSeconds = 0.0f;
        _idleCrossfadeElapsedSeconds = 0.0f;
        _hasQueuedStoppingReplan = false;
        _stoppingReplanSequence = 0L;
        _landingCushionRemainingSeconds = 0.0f;
        _isMovingLanding = false;

        Vector3 currentRoot = _hasDisplayedPose ? _displayedPose[0].Position : Vector3.Zero;
        float invWorldScale = 1.0f / EffectiveWorldScale;

        if (targetProne && _proneTransitionProgress < 1.0f)
        {
            float enterDuration = _proneTransitionUseStandingEndpoint
                ? StandToProneDurationSeconds
                : CrouchToProneDurationSeconds;
            _proneTransitionProgress = MathF.Min(1.0f, _proneTransitionProgress + (dt > 0.0f ? dt / enterDuration : 0.0f));
            _poseBank.BuildProneTransitionPose(
                _proneTransitionProgress,
                _proneTransitionUseStandingEndpoint,
                currentRoot,
                _planner.CurrentFacingYawRadians,
                _airborneScratch);
            BlendDisplayedPosePreservingRootXZ(_airborneScratch, blendRatePerSecond: 18.0f, dt);
            _displayedFootContacts = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
            _hasDisplayedPose = true;
            _history.RecordPose(_displayedPose, dt);
            return;
        }

        if (!targetProne)
        {
            float exitDuration = _proneTransitionUseStandingEndpoint
                ? ProneToStandDurationSeconds
                : ProneToCrouchDurationSeconds;
            _proneTransitionProgress = MathF.Max(0.0f, _proneTransitionProgress - (dt > 0.0f ? dt / exitDuration : 0.0f));
            _poseBank.BuildProneTransitionPose(
                _proneTransitionProgress,
                _proneTransitionUseStandingEndpoint,
                currentRoot,
                _planner.CurrentFacingYawRadians,
                _airborneScratch);
            BlendDisplayedPosePreservingRootXZ(_airborneScratch, blendRatePerSecond: 18.0f, dt);
            _displayedFootContacts = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
            _hasDisplayedPose = true;

            if (_proneTransitionProgress <= 0.0f)
            {
                // Seed clean upright Crouch or Standing history on exit so SOMA never
                // conditions on face-down prone frames when resuming neural locomotion.
                _wasProne = false;
                _idleStyleIndex = input.StyleIndex;
                _lastNonProneStyleIndex = input.StyleIndex;
                _history.SeedFromPose(_displayedPose);

                if (!_planner.IsIdleLocked)
                {
                    EnqueueReplan(input, isUrgent: true);
                }
                else
                {
                    _isCrossfadingToIdle = true;
                    _idleCrossfadeElapsedSeconds = 0.0f;
                    _idleBreathingTimeSeconds = 0.0f;
                    _displayedPose.AsSpan(0, JointCount).CopyTo(_idleCrossfadeStartPose);
                    _idleAnchorRootXZ = new Vector3(_displayedPose[0].Position.X, 0.0f, _displayedPose[0].Position.Z);
                    _idleFacingYawRadians = _planner.CurrentFacingYawRadians;
                }
            }
            else
            {
                _history.RecordPose(_displayedPose, dt);
            }

            return;
        }

        _idleBreathingTimeSeconds += dt;
        float facingYaw = _planner.CurrentFacingYawRadians;
        float turnRate = 0.0f;
        if (_hasLastProneFacingYaw && dt > 1.0e-4f)
        {
            float yawDelta = CriticalSpringDamper.WrapAngleRadians(facingYaw - _lastProneFacingYawRadians);
            turnRate = Math.Clamp(yawDelta / dt, -4.0f, 4.0f);
        }

        _lastProneFacingYawRadians = facingYaw;
        _hasLastProneFacingYaw = true;

        Vector2 worldPlanarVel = _planner.DesiredPlanarVelocity;
        Vector2 somaPlanarVel = worldPlanarVel * invWorldScale;
        float worldSpeed = worldPlanarVel.Length();
        float absTurnRate = MathF.Abs(turnRate);

        if (dt > 0.0f)
        {
            // Reverse crawl cycle when crawling backward relative to facing and keep limbs
            // stepping softly when pivoting in place on the ground.
            Vector2 facingFwdXZ = new(MathF.Sin(facingYaw), MathF.Cos(facingYaw));
            float crawlDirection = worldSpeed > 0.04f && Vector2.Dot(worldPlanarVel, facingFwdXZ) < -0.05f
                ? -1.0f
                : 1.0f;
            float phaseSpeed = worldSpeed * 5.6f * crawlDirection;
            if (worldSpeed <= 0.04f && absTurnRate > 0.08f)
                phaseSpeed = absTurnRate * 2.4f;

            if (MathF.Abs(phaseSpeed) > 1.0e-3f)
            {
                const float twoPi = MathF.PI * 2.0f;
                _proneCrawlPhaseRadians = (_proneCrawlPhaseRadians + phaseSpeed * dt) % twoPi;
                if (_proneCrawlPhaseRadians < 0.0f)
                    _proneCrawlPhaseRadians += twoPi;
            }

            if (worldSpeed > 0.02f)
                currentRoot += new Vector3(somaPlanarVel.X * dt, 0.0f, somaPlanarVel.Y * dt);
        }

        float maxProneSpeed = MathF.Max(0.35f, input.WalkSpeedMetersPerSecond * 0.55f);
        float linearMoveFraction = Math.Clamp(worldSpeed / maxProneSpeed, 0.0f, 1.0f);
        float pivotMoveFraction = Math.Clamp((absTurnRate - 0.08f) / 1.8f, 0.0f, 0.65f);
        float moveFraction = MathF.Max(linearMoveFraction, pivotMoveFraction);
        _poseBank.BuildPronePose(
            _proneCrawlPhaseRadians,
            moveFraction,
            currentRoot,
            facingYaw,
            _airborneScratch,
            _idleBreathingTimeSeconds,
            turnRate);
        BlendDisplayedPosePreservingRootXZ(_airborneScratch, blendRatePerSecond: 12.0f, dt);
        _displayedFootContacts = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        _hasDisplayedPose = true;
        _history.RecordPose(_displayedPose, dt);
    }

    private bool AdvanceGroundedLocomotion(in MotionBricksSteeringInput input, float dt)
    {
        _lastNonProneStyleIndex = input.StyleIndex;
        // If standing or crouched stance changes while idle or winding into idle, exit the
        // breathing idle so SOMA generates a crisp crouch-down or stand-up transition.
        bool styleChangedWhileIdle = (_isInBreathingIdle || _isCrossfadingToIdle || _hasQueuedStoppingReplan)
            && input.StyleIndex != _idleStyleIndex;
        if (styleChangedWhileIdle)
        {
            _isInBreathingIdle = false;
            _isCrossfadingToIdle = false;
            _idleSettledSeconds = 0.0f;
            _idleCrossfadeElapsedSeconds = 0.0f;
            _hasQueuedStoppingReplan = false;
            _stoppingReplanSequence = 0L;
            _idleStyleIndex = input.StyleIndex;
            lock (_swapSync)
            {
                _activeBuffer.Clear();
                _stagingBuffer.Clear();
            }
        }

        bool wantsCalmIdle = (_planner.IsIdleLocked || _planner.IsWallStopped)
            && _landingCushionRemainingSeconds <= 0.0f
            && _wasGrounded
            && !_wasProne;
        if (wantsCalmIdle)
        {
            _idleSettledSeconds += dt;
        }
        else
        {
            _idleSettledSeconds = 0.0f;
            _hasQueuedStoppingReplan = false;
            _stoppingReplanSequence = 0L;
        }

        if (_isInBreathingIdle && wantsCalmIdle)
        {
            _idleBreathingTimeSeconds += dt;
            float idleYaw = input.StrafeMode ? _planner.CurrentFacingYawRadians : _idleFacingYawRadians;
            SampleActiveIdlePose(
                _idleBreathingTimeSeconds,
                input.StyleIndex,
                _idleAnchorRootXZ,
                idleYaw,
                breathingAmplitudeScale: 1.0f,
                _displayedPose);
            _displayedFootContacts = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
            _hasDisplayedPose = true;
            _history.RecordPose(_displayedPose, dt);
            return true;
        }

        bool resumedFromNonLocomotion = !_wasGrounded
            || _wasProne
            || _isInBreathingIdle
            || (_isCrossfadingToIdle && !wantsCalmIdle);
        if (resumedFromNonLocomotion)
            HandleResumeFromNonLocomotion(in input);

        // Adopt any completed trajectory produced by the background worker.
        TrySwapCompletedTrajectory();

        if (wantsCalmIdle)
        {
            // Dispatch one SOMA clip-entry transition toward a real mocap idle clip (or
            // fallback idle template), then play it cleanly into the idle clip loop.
            bool needsStoppingReplan = !_isCrossfadingToIdle
                && !_hasQueuedStoppingReplan
                && (_activeBuffer.FrameCount > 0 || styleChangedWhileIdle || !_planner.IsPlannedStoppedForStyle(input.StyleIndex));
            if (needsStoppingReplan)
            {
                SomaMotionClip? chosenIdleClip = _poseBank.HasIdleClips(input.StyleIndex)
                    ? _poseBank.SelectIdleClip(input.StyleIndex, _activeIdleClip, ref _idleClipRngState)
                    : null;
                bool isStanceChange = styleChangedWhileIdle || !_planner.IsPlannedStoppedForStyle(input.StyleIndex);
                long seq = chosenIdleClip is not null
                    ? EnqueueIdleClipTransitionReplan(input, chosenIdleClip, isStanceChange)
                    : EnqueueReplan(input, isUrgent: true);
                if (seq > 0L)
                {
                    _activeIdleClip = chosenIdleClip;
                    _hasQueuedStoppingReplan = true;
                    _stoppingReplanSequence = seq;
                    _idleStyleIndex = input.StyleIndex;
                }
            }
        }
        else if (_planner.ShouldTriggerReplan(input, _activeBuffer.RemainingFrames, out bool isUrgent))
        {
            EnqueueReplan(input, isUrgent);
        }

        // In synchronous mode, execute immediately and swap.
        if (!_useBackgroundThread)
        {
            ProcessPendingRequestCore();
            TrySwapCompletedTrajectory();
        }

        // Begin the C1-continuous smoothstep cross-fade while SOMA finishes the last ~0.50 s
        // of its braking trajectory so footsteps wind organically into the breathing idle.
        if (wantsCalmIdle && !_isCrossfadingToIdle && _landingCushionRemainingSeconds <= 0.0f)
        {
            bool freshSpawnIdle = _activeBuffer.FrameCount == 0
                && !_hasQueuedStoppingReplan
                && Volatile.Read(ref _contextSeedCount) == 0L
                && _idleSettledSeconds >= InitialSpawnIdleDelaySeconds;
            bool brakingWindingDown = _hasQueuedStoppingReplan
                && _activeBuffer.FrameCount > 0
                && _activeBuffer.GenerationSequence >= _stoppingReplanSequence
                && _activeBuffer.RemainingFrames <= IdleCrossfadeStartRemainingFrames;
            bool fallbackTimedOut = _idleSettledSeconds >= IdleCrossfadeFallbackDelaySeconds;

            if (freshSpawnIdle || brakingWindingDown || fallbackTimedOut)
            {
                _isCrossfadingToIdle = true;
                _idleCrossfadeElapsedSeconds = 0.0f;
                _idleBreathingTimeSeconds = 0.0f;
                _idleStyleIndex = input.StyleIndex;
                if (_hasDisplayedPose)
                {
                    _displayedPose.AsSpan(0, JointCount).CopyTo(_idleCrossfadeStartPose);
                    _idleAnchorRootXZ = new Vector3(_displayedPose[0].Position.X, 0.0f, _displayedPose[0].Position.Z);
                    _idleFacingYawRadians = ExtractFacingYaw(_displayedPose[0].Rotation, _planner.CurrentFacingYawRadians);
                }
                else
                {
                    _poseBank.CopyIdlePoseAtYaw(_planner.CurrentFacingYawRadians, _idleCrossfadeStartPose);
                    _idleAnchorRootXZ = Vector3.Zero;
                    _idleFacingYawRadians = _planner.CurrentFacingYawRadians;
                }
            }
        }

        if (_landingCushionRemainingSeconds > 0.0f)
        {
            AdvanceLandingCushion(in input, dt);
            return true;
        }

        if (_isCrossfadingToIdle)
        {
            AdvanceIdleCrossfade(in input, dt);
            return true;
        }

        if (_activeBuffer.TryAdvanceAndSample(dt, _displayedPose, out Vector4 contacts))
        {
            _displayedFootContacts = contacts;
            _hasDisplayedPose = true;
            _history.RecordPose(_displayedPose, dt);

            if (_activeBuffer.IsExhausted)
                _diagnosticStatus = "Warning: Trajectory buffer exhausted while waiting for TensorRT replan; holding last valid pose.";
            return true;
        }

        return _hasDisplayedPose;
    }

    private void HandleResumeFromNonLocomotion(in MotionBricksSteeringInput input)
    {
        // Just landed, exited prone, or began moving from breathing idle: discard stale
        // buffers and schedule an immediate SOMA replan.
        bool justLanded = !_wasGrounded;
        _wasGrounded = true;
        _wasProne = false;
        _isInBreathingIdle = false;
        _isCrossfadingToIdle = false;
        _idleCrossfadeElapsedSeconds = 0.0f;
        _hasQueuedStoppingReplan = false;
        _stoppingReplanSequence = 0L;
        _idleStyleIndex = input.StyleIndex;

        lock (_swapSync)
        {
            _activeBuffer.Clear();
            _stagingBuffer.Clear();
        }

        if (!justLanded)
        {
            _landingCarryVelocityXZ = Vector2.Zero;
            _landingCushionDurationSeconds = 0.0f;
            _landingCushionRemainingSeconds = 0.0f;
            _isMovingLanding = false;
            EnqueueReplan(input, isUrgent: true);
            return;
        }

        Vector2 carriedWorldVel = _landingCarryVelocityXZ * EffectiveWorldScale;
        _planner.SeedLandingMomentum(carriedWorldVel, in input);

        bool hasMoveInput = input.MoveInput.LengthSquared() > 0.04f;
        bool movingLanding = hasMoveInput || _landingCarryVelocityXZ.LengthSquared() > 0.25f;
        if (hasMoveInput)
        {
            float invWorldScale = 1.0f / EffectiveWorldScale;
            Vector2 desiredSomaVel = _planner.DesiredPlanarVelocity * invWorldScale;
            float desiredSpeed = desiredSomaVel.Length();
            float carriedSpeed = _landingCarryVelocityXZ.Length();
            if (desiredSpeed > 0.05f)
                _landingCarryVelocityXZ = (desiredSomaVel / desiredSpeed) * MathF.Max(carriedSpeed, desiredSpeed);
        }

        if (movingLanding)
        {
            _isMovingLanding = true;
            float maxSpeed = MathF.Max(input.RunSpeedMetersPerSecond, 0.1f);
            _movingLandingNormalizedSpeed = Math.Clamp(
                (_landingCarryVelocityXZ.Length() * EffectiveWorldScale) / maxSpeed,
                0.25f,
                1.0f);
            _landingCushionDurationSeconds = 0.10f;
            _landingCushionRemainingSeconds = 0.10f;

            // Seed 4 velocity-consistent moving stride context poses so SOMA
            // continues straight into walking or running without restarting from idle.
            Vector3 currentRootXZ = _hasDisplayedPose
                ? new Vector3(_displayedPose[0].Position.X, 0.0f, _displayedPose[0].Position.Z)
                : Vector3.Zero;
            _poseBank.BuildMovingLandingContextFrames(
                _movingLandingNormalizedSpeed,
                _landingCarryVelocityXZ,
                currentRootXZ,
                _planner.CurrentFacingYawRadians,
                input.StyleIndex,
                _landingContextScratch);
            _history.SeedFromFourPoses(_landingContextScratch);

            EnqueueReplan(input, isUrgent: false);
        }
        else
        {
            _isMovingLanding = false;
            _landingCushionDurationSeconds = 0.15f;
            _landingCushionRemainingSeconds = 0.15f;
            EnqueueReplan(input, isUrgent: true);
        }
    }

    private void AdvanceLandingCushion(in MotionBricksSteeringInput input, float dt)
    {
        _landingCushionRemainingSeconds = MathF.Max(0.0f, _landingCushionRemainingSeconds - dt);

        if (_isMovingLanding && !_planner.IsIdleLocked && dt > 0.0f)
        {
            float invWorldScale = 1.0f / EffectiveWorldScale;
            Vector2 desiredSomaVel = _planner.DesiredPlanarVelocity * invWorldScale;
            if (desiredSomaVel.LengthSquared() > 0.01f)
            {
                float carrySpeed = MathF.Max(_landingCarryVelocityXZ.Length(), desiredSomaVel.Length());
                _landingCarryVelocityXZ = Vector2.Normalize(desiredSomaVel) * carrySpeed;
            }
        }
        else if (_planner.IsIdleLocked && dt > 0.0f)
        {
            _landingCarryVelocityXZ *= MathF.Exp(-5.5f * dt);
        }

        Vector3 currentRoot = _hasDisplayedPose ? _displayedPose[0].Position : Vector3.Zero;
        if (dt > 0.0f)
            currentRoot += new Vector3(_landingCarryVelocityXZ.X * dt, 0.0f, _landingCarryVelocityXZ.Y * dt);

        float cushionDuration = MathF.Max(0.05f, _landingCushionDurationSeconds);
        float landingProgress = 1.0f - Math.Clamp(_landingCushionRemainingSeconds / cushionDuration, 0.0f, 1.0f);
        if (_isMovingLanding && !_planner.IsIdleLocked)
        {
            _poseBank.BuildMovingLandingPose(
                _movingLandingNormalizedSpeed,
                landingProgress,
                currentRoot,
                _planner.CurrentFacingYawRadians,
                _airborneScratch,
                input.StyleIndex);
            BlendDisplayedPosePreservingRootXZ(_airborneScratch, blendRatePerSecond: 22.0f, dt);
        }
        else
        {
            _poseBank.BuildLandingPose(landingProgress, currentRoot, _planner.CurrentFacingYawRadians, _airborneScratch);
            BlendDisplayedPosePreservingRootXZ(_airborneScratch, blendRatePerSecond: 18.0f, dt);
        }

        _displayedFootContacts = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        _hasDisplayedPose = true;
        if (!_isMovingLanding)
            _history.RecordPose(_displayedPose, dt);

        // Hand off early into SOMA's full-body stride once the landing replan arrives.
        if (_activeBuffer.FrameCount > 0 && _landingCushionRemainingSeconds <= 0.04f)
        {
            _landingCushionRemainingSeconds = 0.0f;
            _isMovingLanding = false;
        }
    }

    private void AdvanceIdleCrossfade(in MotionBricksSteeringInput input, float dt)
    {
        Vector4 somaContacts = new(1.0f, 1.0f, 1.0f, 1.0f);
        if (_activeBuffer.FrameCount > 0
            && _activeBuffer.TryAdvanceAndSample(dt, _somaIdleScratch, out Vector4 sampledContacts))
        {
            somaContacts = sampledContacts;
            _idleAnchorRootXZ = new Vector3(_somaIdleScratch[0].Position.X, 0.0f, _somaIdleScratch[0].Position.Z);
            _idleFacingYawRadians = input.StrafeMode
                ? _planner.CurrentFacingYawRadians
                : ExtractFacingYaw(_somaIdleScratch[0].Rotation, _idleFacingYawRadians);
        }
        else
        {
            // Lerp from the frozen cross-fade entry pose (never from the mutating
            // _displayedPose) so clip root yaw never feeds back into itself and spins.
            _idleCrossfadeStartPose.AsSpan(0, JointCount).CopyTo(_somaIdleScratch);
            if (input.StrafeMode)
                _idleFacingYawRadians = _planner.CurrentFacingYawRadians;
        }

        _idleCrossfadeElapsedSeconds += dt;
        _idleBreathingTimeSeconds += dt;
        float u = Math.Clamp(_idleCrossfadeElapsedSeconds / IdleCrossfadeDurationSeconds, 0.0f, 1.0f);
        float w = u * u * (3.0f - 2.0f * u);

        Vector3 liveRootXZ = _idleAnchorRootXZ;
        float liveYaw = _idleFacingYawRadians;

        SampleActiveIdlePose(
            _idleBreathingTimeSeconds,
            input.StyleIndex,
            liveRootXZ,
            liveYaw,
            breathingAmplitudeScale: w,
            _airborneScratch);

        for (int joint = 0; joint < JointCount; joint++)
            _displayedPose[joint] = SomaPoseTransform.Lerp(_somaIdleScratch[joint], _airborneScratch[joint], w);

        _displayedFootContacts = Vector4.Lerp(somaContacts, new Vector4(1.0f, 1.0f, 1.0f, 1.0f), w);
        _hasDisplayedPose = true;
        _history.RecordPose(_displayedPose, dt);

        if (u >= 1.0f)
        {
            _isCrossfadingToIdle = false;
            _isInBreathingIdle = true;
            _hasQueuedStoppingReplan = false;
            _stoppingReplanSequence = 0L;
            _idleAnchorRootXZ = liveRootXZ;
            _idleFacingYawRadians = liveYaw;
            lock (_swapSync)
            {
                _activeBuffer.Clear();
                _stagingBuffer.Clear();
            }
        }
    }

    private void BlendDisplayedPosePreservingRootXZ(
        ReadOnlySpan<SomaPoseTransform> targetPose,
        float blendRatePerSecond,
        float dt)
    {
        if (!_hasDisplayedPose || dt <= 0.0f)
        {
            targetPose.CopyTo(_displayedPose);
            return;
        }

        float alpha = 1.0f - MathF.Exp(-blendRatePerSecond * dt);
        SomaPoseTransform prevRoot = _displayedPose[0];
        SomaPoseTransform targetRoot = targetPose[0];
        // Apply target root XZ directly so exponential pose smoothing never attenuates
        // prone crawl or moving-landing root translation velocity.
        float blendedY = prevRoot.Position.Y + (targetRoot.Position.Y - prevRoot.Position.Y) * alpha;
        Quaternion blendedRot = SomaPoseTransform.Lerp(prevRoot, targetRoot, alpha).Rotation;
        _displayedPose[0] = new SomaPoseTransform(
            new Vector3(targetRoot.Position.X, blendedY, targetRoot.Position.Z),
            blendedRot);

        for (int i = 1; i < JointCount; i++)
            _displayedPose[i] = SomaPoseTransform.Lerp(_displayedPose[i], targetPose[i], alpha);
    }

    private Vector2 ResolveAirbornePlanarVelocityWorld()
    {
        if (_hasLiveWorldFootPosition)
        {
            var physicsVelXZ = new Vector2(_liveActualPlanarVelocity.X, _liveActualPlanarVelocity.Z);
            if (float.IsFinite(physicsVelXZ.X) && float.IsFinite(physicsVelXZ.Y) && physicsVelXZ.LengthSquared() > 0.0025f)
                return physicsVelXZ;
        }

        return _planner.DesiredPlanarVelocity;
    }

    private float ExtractFacingYaw(Quaternion somaRootRotation, float fallbackYawRadians)
    {
        // Strip the SOMA rest-pose root basis (where local +Z maps to +X on SOMA N1) before
        // reading canonical +Z facing yaw so idle handoff keeps the exact character orientation.
        Quaternion canonicalBodyRotation = SomaPoseTransform.NormalizeOrIdentity(
            somaRootRotation * _calibration.InverseRestWorldRotations[0]);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, canonicalBodyRotation);
        float lenSq = forward.X * forward.X + forward.Z * forward.Z;
        if (lenSq >= 0.25f)
            return MathF.Atan2(forward.X, forward.Z);

        Vector3 right = Vector3.Transform(Vector3.UnitX, canonicalBodyRotation);
        Vector3 planarForward = Vector3.Cross(right, Vector3.UnitY);
        float planarLenSq = planarForward.X * planarForward.X + planarForward.Z * planarForward.Z;
        if (planarLenSq > 1.0e-8f)
            return MathF.Atan2(planarForward.X, planarForward.Z);

        return lenSq > 1.0e-8f ? MathF.Atan2(forward.X, forward.Z) : fallbackYawRadians;
    }

    private void SampleActiveIdlePose(
        float idleTimeSeconds,
        int styleIndex,
        Vector3 anchorRootXZ,
        float facingYawRadians,
        float breathingAmplitudeScale,
        Span<SomaPoseTransform> destination)
    {
        if (_poseBank.HasIdleClips(styleIndex))
        {
            if (_activeIdleClip is null || _idleStyleIndex != styleIndex)
            {
                _activeIdleClip = _poseBank.SelectIdleClip(styleIndex, _activeIdleClip, ref _idleClipRngState);
                _idleStyleIndex = styleIndex;
            }

            if (_activeIdleClip is not null)
            {
                _activeIdleClip.SamplePlacedFrame(
                    idleTimeSeconds,
                    loop: true,
                    anchorRootXZ,
                    facingYawRadians,
                    destination);
                return;
            }
        }

        _poseBank.BuildBreathingIdlePose(
            idleTimeSeconds,
            styleIndex,
            anchorRootXZ,
            facingYawRadians,
            destination,
            breathingAmplitudeScale);
    }

    private void TrySwapCompletedTrajectory()
    {
        lock (_swapSync)
        {
            if (_stagedSequence <= _consumedSequence || _stagingBuffer.FrameCount <= 0)
                return;

            Vector2 previousRootVelocityXZ = _activeBuffer.LastRootVelocityXZ;
            (_activeBuffer, _stagingBuffer) = (_stagingBuffer, _activeBuffer);
            _consumedSequence = _stagedSequence;
            long requestTimestamp = _stagedRequestTimestamp;
            if (_hasDisplayedPose)
            {
                float latencySeconds = _useBackgroundThread && requestTimestamp > 0L
                    ? (float)((Stopwatch.GetTimestamp() - requestTimestamp) / (double)Stopwatch.Frequency)
                    : 0.0f;
                _activeBuffer.AdvanceToLatencyAndAlignRootXZ(latencySeconds, _displayedPose[0].Position);
                // Use a gentle 0.26 s cross-blend during calm idle swaps so consecutive idle cycles
                // flow organically without robotic twitching; keep crisp 0.11 s splices while moving.
                float spliceSeconds = _planner.IsIdleLocked
                    ? MathF.Max(_settings.SpliceBlendDurationSeconds, 0.26f)
                    : _settings.SpliceBlendDurationSeconds;
                _activeBuffer.BeginSpliceFrom(
                    _displayedPose,
                    _displayedFootContacts,
                    spliceSeconds,
                    previousRootVelocityXZ);
            }
        }
    }

    private Vector3 ResolveWorldFootOrigin()
    {
        if (_hasLiveWorldFootPosition)
            return _liveWorldFootPosition;

        if (_hasDisplayedPose)
        {
            float worldPerSoma = EffectiveWorldScale;
            return new Vector3(
                _somaWorldOriginOffsetXZ.X + _displayedPose[0].Position.X * worldPerSoma,
                0.0f,
                _somaWorldOriginOffsetXZ.Z + _displayedPose[0].Position.Z * worldPerSoma);
        }

        return Vector3.Zero;
    }

    private void PublishDebugSnapshot()
    {
        if (!_hasDisplayedPose)
            return;

        bool hasMid = _activeBuffer.TrySampleFuturePose(0.30f, _debugPoseScratch, out float midLead);
        lock (_debugSync)
        {
            _displayedPose.AsSpan(0, JointCount).CopyTo(_debugDisplayedPoseSnapshot);
            _debugFootContactsSnapshot = _displayedFootContacts;
            _debugHasCurrentPose = true;

            _debugHasFutureMidPose = hasMid && midLead > 0.03f;
            if (_debugHasFutureMidPose)
            {
                _debugPoseScratch.AsSpan(0, JointCount).CopyTo(_debugFutureMidPoseSnapshot);
                _debugFutureMidLeadSeconds = midLead;
            }

            bool hasEnd = _activeBuffer.TrySampleFuturePose(10.0f, _debugPoseScratch, out float endLead);
            _debugHasFutureEndPose = hasEnd && endLead > 0.08f;
            if (_debugHasFutureEndPose)
            {
                _debugPoseScratch.AsSpan(0, JointCount).CopyTo(_debugFutureEndPoseSnapshot);
                _debugFutureEndLeadSeconds = endLead;
            }

            _debugTrajectoryCount = _planner.CopyProjectedTrajectory(_debugTrajectorySnapshot);
            _debugWorldFootAnchor = ResolveWorldFootOrigin();
            _debugTargetGroundWorld = _planner.LastTargetGroundWorldPosition;
        }
    }

    private void ComposeWorldSkeletonJoints(
        ReadOnlySpan<SomaPoseTransform> localPose,
        Vector3 currentSomaRootXZ,
        Vector3 worldFootAnchor,
        float legScale,
        Vector3[] destinationJoints)
    {
        SomaTensorCodec.ComputeWorldPose(localPose, SomaPoseTransform.Identity, _debugWorldScratch);
        int count = Math.Min(JointCount, destinationJoints.Length);
        for (int joint = 0; joint < count; joint++)
        {
            Vector3 relativeSoma = _debugWorldScratch[joint].Position - currentSomaRootXZ;
            destinationJoints[joint] = worldFootAnchor + relativeSoma * legScale;
        }
    }
}

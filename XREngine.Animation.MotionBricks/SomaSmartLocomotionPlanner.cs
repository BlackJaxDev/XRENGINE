using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Allocation-free C# Smart Locomotion planner that converts joystick / WASD steering
/// parameters into smoothed root trajectories, replan triggers, dynamic token horizons,
/// and placed SOMA target poses for idle, walk, jog, run, turns, and strafing.
/// </summary>
public sealed class SomaSmartLocomotionPlanner
{
    private readonly SomaSmartLocomotionSettings _settings;
    private readonly SomaLocomotionPoseBank _poseBank;

    private bool _isMovingInputActive;
    private float _desiredSpeed;
    private float _desiredSpeedRate;
    private float _desiredVelocityYaw;
    private float _desiredVelocityYawRate;
    private float _currentFacingYaw;
    private float _currentFacingYawRate;
    private Vector2 _inputVelocityDirection = new(0.0f, 1.0f);

    private float _timeSinceLastReplan;
    private float _lastPlannedSpeed;
    private Vector2 _lastPlannedDirection = new(0.0f, 1.0f);
    private bool _lastPlannedStrafeMode;
    private int _lastPlannedStyleIndex;
    private bool _physicsReplanRequested;
    private bool _isWallStopped;
    private Vector2 _wallBlockNormalXZ;
    private float _wallBlockHoldSeconds;

    private readonly MotionBricksTrajectoryPoint[] _projectedTrajectory =
        new MotionBricksTrajectoryPoint[MotionBricksDebugSnapshot.MaxTrajectoryPoints];
    private int _projectedTrajectoryCount;
    private Vector3 _lastTargetGroundWorldPosition;
    private bool _lastTrajectoryHitObstacle;

    public SomaSmartLocomotionPlanner(
        SomaSmartLocomotionSettings? settings = null,
        SomaLocomotionPoseBank? poseBank = null)
    {
        _settings = settings ?? SomaSmartLocomotionSettings.Default;
        _poseBank = poseBank ?? new SomaLocomotionPoseBank();
    }

    public float DesiredSpeed => _desiredSpeed;
    public float DesiredVelocityYawRadians => _desiredVelocityYaw;
    public float CurrentFacingYawRadians => _currentFacingYaw;
    public bool IsIdleLocked => !_isMovingInputActive && _desiredSpeed <= _settings.IdleLockSpeedThreshold;
    public bool IsWallStopped => _isWallStopped;
    public int ProjectedTrajectoryCount => _projectedTrajectoryCount;
    public Vector3 LastTargetGroundWorldPosition => _lastTargetGroundWorldPosition;

    /// <summary>
    /// Returns true when the last planned trajectory targeted a stopped pose matching
    /// <paramref name="styleIndex"/>.
    /// </summary>
    public bool IsPlannedStoppedForStyle(int styleIndex)
        => _lastPlannedSpeed <= _settings.IdleLockSpeedThreshold && _lastPlannedStyleIndex == styleIndex;

    public int CopyProjectedTrajectory(Span<MotionBricksTrajectoryPoint> destination)
    {
        int count = Math.Min(_projectedTrajectoryCount, destination.Length);
        if (count > 0)
            _projectedTrajectory.AsSpan(0, count).CopyTo(destination);
        return count;
    }

    public Vector2 DesiredPlanarVelocity => new(
        MathF.Sin(_desiredVelocityYaw) * _desiredSpeed,
        MathF.Cos(_desiredVelocityYaw) * _desiredSpeed);

    /// <summary>
    /// Seeds horizontal speed and travel direction on touchdown from airborne momentum so
    /// landing replans continue smoothly at carried running or walking speed instead of braking.
    /// </summary>
    public void SeedLandingMomentum(Vector2 worldPlanarVelocity, float maxSpeedMetersPerSecond)
    {
        if (!float.IsFinite(worldPlanarVelocity.X) || !float.IsFinite(worldPlanarVelocity.Y))
            return;

        float carrySpeed = worldPlanarVelocity.Length();
        if (carrySpeed <= 0.15f)
            return;

        float clampedSpeed = MathF.Min(carrySpeed, MathF.Max(0.1f, maxSpeedMetersPerSecond));
        if (clampedSpeed > _desiredSpeed)
        {
            _desiredSpeed = clampedSpeed;
            _desiredSpeedRate = 0.0f;
        }

        if (!_isMovingInputActive)
        {
            _desiredVelocityYaw = MathF.Atan2(worldPlanarVelocity.X, worldPlanarVelocity.Y);
            _desiredVelocityYawRate = 0.0f;
        }
    }

    /// <summary>
    /// Seeds horizontal speed and heading on touchdown from carried airborne momentum and active
    /// steering input so landing with forward held continues straight into walking or running
    /// with zero speed loss.
    /// </summary>
    public void SeedLandingMomentum(
        Vector2 worldPlanarVelocity,
        in MotionBricksSteeringInput input)
    {
        ResolveSteeringGoals(input, out float maxSpeed, out float inputSpeed, out float inputYaw);

        float carrySpeed = float.IsFinite(worldPlanarVelocity.X) && float.IsFinite(worldPlanarVelocity.Y)
            ? worldPlanarVelocity.Length()
            : 0.0f;

        if (_isMovingInputActive)
        {
            float targetSpeed = Math.Clamp(MathF.Max(_desiredSpeed, MathF.Max(carrySpeed, inputSpeed)), 0.0f, maxSpeed);
            _desiredSpeed = targetSpeed;
            _desiredSpeedRate = 0.0f;
            // Seed travel direction from carried momentum when airborne motion was active so
            // landing turns smoothly into the held key instead of snapping character facing.
            if (carrySpeed > 0.25f)
            {
                _desiredVelocityYaw = MathF.Atan2(worldPlanarVelocity.X, worldPlanarVelocity.Y);
                _desiredVelocityYawRate = 0.0f;
            }
            else
            {
                _desiredVelocityYaw = inputYaw;
                _desiredVelocityYawRate = 0.0f;
            }
            return;
        }

        SeedLandingMomentum(worldPlanarVelocity, maxSpeed);
    }

    public void Reset(float initialFacingYawRadians = 0.0f)
    {
        float yaw = CriticalSpringDamper.WrapAngleRadians(initialFacingYawRadians);
        _isMovingInputActive = false;
        _desiredSpeed = 0.0f;
        _desiredSpeedRate = 0.0f;
        _desiredVelocityYaw = yaw;
        _desiredVelocityYawRate = 0.0f;
        _currentFacingYaw = yaw;
        _currentFacingYawRate = 0.0f;
        _inputVelocityDirection = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
        _timeSinceLastReplan = 0.0f;
        _lastPlannedSpeed = 0.0f;
        _lastPlannedDirection = _inputVelocityDirection;
        _lastPlannedStrafeMode = false;
        _lastPlannedStyleIndex = SomaLocomotionPoseBank.StandingStyleIndex;
        _physicsReplanRequested = false;
        _isWallStopped = false;
        _wallBlockNormalXZ = Vector2.Zero;
        _wallBlockHoldSeconds = 0.0f;
        _projectedTrajectoryCount = 0;
        _lastTargetGroundWorldPosition = Vector3.Zero;
        _lastTrajectoryHitObstacle = false;
    }

    /// <summary>
    /// Updates smoothed speed, travel heading, and character facing direction for one frame.
    /// </summary>
    public void UpdateSteering(in MotionBricksSteeringInput input)
    {
        float dt = float.IsFinite(input.DeltaSeconds) && input.DeltaSeconds > 0.0f
            ? input.DeltaSeconds
            : 0.0f;
        _timeSinceLastReplan += dt;

        float rawMag = float.IsFinite(input.MoveInput.X) && float.IsFinite(input.MoveInput.Y)
            ? input.MoveInput.Length()
            : 0.0f;

        // Hysteresis prevents stick jitter around zero from causing micro-steps.
        if (_isMovingInputActive)
        {
            if (rawMag < _settings.StickDeadzoneExit)
                _isMovingInputActive = false;
        }
        else
        {
            if (rawMag >= _settings.StickDeadzoneEnter)
                _isMovingInputActive = true;
        }

        ResolveSpeedBands(input, out float walkSpeed, out float jogSpeed, out float maxSpeed);
        float activeTopSpeed = input.SprintHeld ? maxSpeed : jogSpeed;
        float inputSpeed = 0.0f;
        float inputYaw = _desiredVelocityYaw;

        if (_isMovingInputActive && rawMag > 1.0e-5f)
        {
            float clampedMag = Math.Clamp(rawMag, 0.0f, 1.0f);
            Vector2 normalizedMove = input.MoveInput / rawMag;
            _inputVelocityDirection = normalizedMove;
            inputYaw = MathF.Atan2(normalizedMove.X, normalizedMove.Y);

            // Map 0..0.6 smoothly to walk speed; 0.6..1.0 maps to jog speed normally, or to full run
            // speed when SprintHeld is active (Shift on keyboard, X / Square held on gamepad).
            inputSpeed = clampedMag <= 0.6f
                ? walkSpeed * (clampedMag / 0.6f)
                : walkSpeed + (activeTopSpeed - walkSpeed) * ((clampedMag - 0.6f) / 0.4f);
        }

        if (_isMovingInputActive)
        {
            CriticalSpringDamper.StepAngle(
                ref _desiredVelocityYaw,
                ref _desiredVelocityYawRate,
                inputYaw,
                _settings.VelocityDirectionHalflifeSeconds,
                dt);
        }

        float targetFacingYaw = input.StrafeMode
            ? CriticalSpringDamper.WrapAngleRadians(input.DesiredFacingYawRadians)
            : _desiredVelocityYaw;
        CriticalSpringDamper.StepAngle(
            ref _currentFacingYaw,
            ref _currentFacingYawRate,
            targetFacingYaw,
            _settings.FacingHalflifeSeconds,
            dt);

        // Slow down smoothly during sharp turns until travel heading catches up.
        float speedGoal = inputSpeed;
        if (_wallBlockHoldSeconds > 0.0f)
        {
            _wallBlockHoldSeconds = MathF.Max(0.0f, _wallBlockHoldSeconds - dt);
            bool pushingIntoWall = _isMovingInputActive
                && _wallBlockNormalXZ.LengthSquared() > 1.0e-4f
                && Vector2.Dot(_inputVelocityDirection, _wallBlockNormalXZ) < -0.50f;
            if (pushingIntoWall)
            {
                speedGoal = 0.0f;
                _isWallStopped = true;
                _wallBlockHoldSeconds = MathF.Max(_wallBlockHoldSeconds, 0.20f);
            }
            else if (_wallBlockHoldSeconds <= 0.0f || !_isMovingInputActive || !pushingIntoWall)
            {
                _isWallStopped = false;
                _wallBlockHoldSeconds = 0.0f;
            }
        }
        else
        {
            _isWallStopped = false;
        }

        if (_isMovingInputActive && !_isWallStopped)
        {
            var smoothedDir = new Vector2(MathF.Sin(_desiredVelocityYaw), MathF.Cos(_desiredVelocityYaw));
            float turnDot = Vector2.Dot(smoothedDir, _inputVelocityDirection);
            if (turnDot < _settings.DirectionRegenerationDot)
            {
                float speedFraction = maxSpeed > 1.0e-4f ? Math.Clamp(_desiredSpeed / maxSpeed, 0.0f, 1.0f) : 0.0f;
                float reduction = 1.0f - _settings.MaxTurnSpeedReduction * speedFraction;
                speedGoal *= Math.Clamp(reduction, 0.25f, 1.0f);
            }
        }

        float speedHalflife = speedGoal > _desiredSpeed
            ? _settings.SpeedAccelerationHalflifeSeconds
            : _settings.SpeedDecelerationHalflifeSeconds;
        CriticalSpringDamper.StepScalar(ref _desiredSpeed, ref _desiredSpeedRate, speedGoal, speedHalflife, dt);
        _desiredSpeed = Math.Clamp(_desiredSpeed, 0.0f, maxSpeed);
        if ((!_isMovingInputActive || _isWallStopped) && _desiredSpeed < _settings.IdleLockSpeedThreshold)
        {
            _desiredSpeed = 0.0f;
            _desiredSpeedRate = 0.0f;
        }
    }

    /// <summary>
    /// Applies feedback from Jolt collision blocking so the planner stops cleanly against head-on
    /// walls or slides smoothly along oblique walls without spamming urgent replans.
    /// </summary>
    public void NotifyPhysicsCollisionClamp(Vector3 actualPlanarVelocity, Vector3 wallNormal, bool isBlocked)
    {
        if (!isBlocked)
            return;

        bool wasWallStopped = _isWallStopped;
        float actualSpeed = MathF.Sqrt(
            actualPlanarVelocity.X * actualPlanarVelocity.X
            + actualPlanarVelocity.Z * actualPlanarVelocity.Z);
        if (actualSpeed < _desiredSpeed)
            _desiredSpeed = actualSpeed;

        // Project desired travel direction onto the wall tangent when colliding obliquely, or
        // latch a calm standing stop when pushing head-on into a solid wall.
        var normalXZ = new Vector2(wallNormal.X, wallNormal.Z);
        float normalLenSq = normalXZ.LengthSquared();
        bool headOnStop = false;
        if (normalLenSq > 1.0e-6f)
        {
            normalXZ /= MathF.Sqrt(normalLenSq);
            var pushDir = _isMovingInputActive
                ? _inputVelocityDirection
                : new Vector2(MathF.Sin(_desiredVelocityYaw), MathF.Cos(_desiredVelocityYaw));
            float intoWall = Vector2.Dot(pushDir, normalXZ);
            if (intoWall < -0.55f && actualSpeed < 0.25f)
            {
                headOnStop = true;
                _isWallStopped = true;
                _wallBlockNormalXZ = normalXZ;
                _wallBlockHoldSeconds = 0.35f;
                _desiredSpeed = 0.0f;
                _desiredSpeedRate = 0.0f;
            }
            else if (intoWall < 0.0f)
            {
                Vector2 tangent = pushDir - normalXZ * intoWall;
                if (tangent.LengthSquared() > 0.04f)
                {
                    tangent = Vector2.Normalize(tangent);
                    _desiredVelocityYaw = MathF.Atan2(tangent.X, tangent.Y);
                    _desiredVelocityYawRate = 0.0f;
                }
            }
        }
        else if (actualSpeed < 0.20f)
        {
            headOnStop = true;
            _isWallStopped = true;
            _wallBlockHoldSeconds = 0.35f;
            _desiredSpeed = 0.0f;
            _desiredSpeedRate = 0.0f;
        }

        // Request an urgent replan on the initial wall stop or oblique slide, but avoid
        // re-triggering urgent replans every physics step while already stopped at the wall.
        if (!headOnStop || !wasWallStopped)
            _physicsReplanRequested = true;
    }

    /// <summary>
    /// Evaluates whether a new MotionBricks replan should be dispatched now.
    /// </summary>
    public bool ShouldTriggerReplan(
        in MotionBricksSteeringInput input,
        int remainingBufferFrames,
        out bool isUrgentTurnOrStop)
    {
        isUrgentTurnOrStop = false;
        if (remainingBufferFrames <= 0)
            return true;

        if (_physicsReplanRequested && _timeSinceLastReplan >= _settings.MinimumReplanCooldownSeconds)
        {
            isUrgentTurnOrStop = true;
            return true;
        }

        if (remainingBufferFrames <= _settings.TransitionEarlyByFrames)
            return true;

        if (input.StyleIndex != _lastPlannedStyleIndex)
        {
            isUrgentTurnOrStop = true;
            return true;
        }

        if (_timeSinceLastReplan < _settings.MinimumReplanCooldownSeconds)
            return false;

        if (input.StrafeMode != _lastPlannedStrafeMode)
        {
            isUrgentTurnOrStop = true;
            return true;
        }

        // Trigger an immediate urgent replan on start-from-idle or release-to-idle transitions
        // so keyboard movement starts and stops without waiting for the steady replan cadence.
        bool wasPlannedIdle = _lastPlannedSpeed <= _settings.IdleLockSpeedThreshold;
        if (_isMovingInputActive && !_isWallStopped && wasPlannedIdle)
        {
            isUrgentTurnOrStop = true;
            return true;
        }

        if ((!_isMovingInputActive || _isWallStopped) && !wasPlannedIdle && _desiredSpeed <= _settings.IdleLockSpeedThreshold)
        {
            isUrgentTurnOrStop = true;
            return true;
        }

        float maxSpeed = MathF.Max(input.RunSpeedMetersPerSecond, 0.1f);
        float speedDelta = MathF.Abs(_desiredSpeed - _lastPlannedSpeed);
        if (speedDelta >= MathF.Max(input.InputReplanThreshold, maxSpeed * _settings.SpeedRegenerationFraction))
        {
            // Keep run-to-walk and walk-to-run speed transitions on the full steady horizon
            // instead of a short urgent brake window so SOMA decelerates cleanly without stumbling.
            isUrgentTurnOrStop = !_isMovingInputActive || _isWallStopped;
            return true;
        }

        if (_isMovingInputActive && !_isWallStopped)
        {
            float directionDot = Vector2.Dot(_inputVelocityDirection, _lastPlannedDirection);
            if (directionDot < 0.0f)
            {
                // Sharp reversals (> 90 deg) trigger an immediate urgent replan.
                isUrgentTurnOrStop = true;
                return true;
            }

            if (directionDot < _settings.DirectionRegenerationDot
                && _timeSinceLastReplan >= _settings.ModerateTurnReplanCooldownSeconds)
            {
                // Moderate diagonal steering changes keep the steady horizon and wait for the
                // active splice blend to settle before requesting another replan.
                isUrgentTurnOrStop = false;
                return true;
            }
        }

        // Let settled idle trajectories play out their full 1.2-1.4 s buffer instead of
        // interrupting and re-splicing every 0.40 s, which causes robotic twitching.
        if ((IsIdleLocked || _isWallStopped) && wasPlannedIdle)
            return false;

        // When real mocap target tokens are loaded, match NVIDIA's event-driven replan cadence:
        // let each chunk play down to TransitionEarlyByFrames (~0.8-1.0 s) and replan early only
        // when speed, direction, style, or physics collisions change.
        if (_poseBank.HasMocapTargets)
            return false;

        return _timeSinceLastReplan >= input.ReplanIntervalSeconds;
    }

    /// <summary>
    /// Integrates future ground trajectory points along the player's steering curve, probing
    /// static world geometry via <paramref name="prober"/> when available so future dots snap to
    /// terrain height and slide or stop along upcoming obstacles.
    /// </summary>
    public int ProjectFutureTrajectory(
        in MotionBricksSteeringInput input,
        Vector3 worldFootOrigin,
        float avatarLegScale,
        IMotionBricksTrajectoryProber? prober,
        bool isUrgentTurnOrStop = false)
    {
        int sampleCount = Math.Clamp(_settings.TrajectorySampleCount, 2, MotionBricksDebugSnapshot.MaxTrajectoryPoints);
        bool styleChanged = input.StyleIndex != _lastPlannedStyleIndex;
        float horizonSeconds = styleChanged
            ? _settings.LookaheadSeconds * 0.58f
            : isUrgentTurnOrStop
                ? _settings.LookaheadSeconds * 0.76f
                : _settings.LookaheadSeconds;
        float dtStep = horizonSeconds / sampleCount;
        float legScale = float.IsFinite(avatarLegScale) && avatarLegScale > 0.01f ? avatarLegScale : 1.0f;

        ResolveSteeringGoals(input, out float maxSpeed, out float inputSpeed, out float inputYaw);

        float simSpeed = _desiredSpeed;
        float simSpeedRate = _desiredSpeedRate;
        float simTravelYaw = _desiredVelocityYaw;
        float simTravelYawRate = _desiredVelocityYawRate;
        float simFacingYaw = _currentFacingYaw;
        float simFacingYawRate = _currentFacingYawRate;

        Vector3 cursorWorld = worldFootOrigin;
        Vector3 cursorNormal = Vector3.UnitY;
        bool hasAnyObstacle = _isWallStopped;
        bool haltedByObstacle = _isWallStopped;

        if (prober is not null && prober.ProbeGroundHeight(cursorWorld, 0.50f, 1.50f, out float startGroundY, out Vector3 startNormal))
        {
            cursorWorld.Y = startGroundY;
            cursorNormal = startNormal;
        }

        for (int i = 0; i < sampleCount; i++)
        {
            if (haltedByObstacle)
            {
                simSpeed = 0.0f;
                simSpeedRate = 0.0f;
                _projectedTrajectory[i] = new MotionBricksTrajectoryPoint(
                    cursorWorld,
                    cursorNormal,
                    simFacingYaw,
                    simTravelYaw,
                    0.0f,
                    dtStep * (i + 1),
                    true,
                    true);
                continue;
            }

            if (_isMovingInputActive)
            {
                CriticalSpringDamper.StepAngle(
                    ref simTravelYaw,
                    ref simTravelYawRate,
                    inputYaw,
                    _settings.VelocityDirectionHalflifeSeconds,
                    dtStep);
            }

            float targetFacingYaw = input.StrafeMode
                ? CriticalSpringDamper.WrapAngleRadians(input.DesiredFacingYawRadians)
                : simTravelYaw;
            CriticalSpringDamper.StepAngle(
                ref simFacingYaw,
                ref simFacingYawRate,
                targetFacingYaw,
                _settings.FacingHalflifeSeconds,
                dtStep);

            float speedGoal = inputSpeed;
            if (_isMovingInputActive)
            {
                var smoothedDir = new Vector2(MathF.Sin(simTravelYaw), MathF.Cos(simTravelYaw));
                float turnDot = Vector2.Dot(smoothedDir, _inputVelocityDirection);
                if (turnDot < _settings.DirectionRegenerationDot)
                {
                    float speedFraction = maxSpeed > 1.0e-4f ? Math.Clamp(simSpeed / maxSpeed, 0.0f, 1.0f) : 0.0f;
                    float reduction = 1.0f - _settings.MaxTurnSpeedReduction * speedFraction;
                    speedGoal *= Math.Clamp(reduction, 0.25f, 1.0f);
                }
            }

            float speedHalflife = speedGoal > simSpeed
                ? _settings.SpeedAccelerationHalflifeSeconds
                : _settings.SpeedDecelerationHalflifeSeconds;
            CriticalSpringDamper.StepScalar(ref simSpeed, ref simSpeedRate, speedGoal, speedHalflife, dtStep);
            simSpeed = Math.Clamp(simSpeed, 0.0f, maxSpeed);
            if (!_isMovingInputActive && simSpeed < _settings.IdleLockSpeedThreshold)
            {
                simSpeed = 0.0f;
                simSpeedRate = 0.0f;
            }

            float worldStepDistance = simSpeed * _settings.VelocityScale * _settings.PositionScale * legScale * dtStep;
            var stepXZ = new Vector3(
                MathF.Sin(simTravelYaw) * worldStepDistance,
                0.0f,
                MathF.Cos(simTravelYaw) * worldStepDistance);

            bool stepHitObstacle = false;
            bool stepHasGround = false;
            Vector3 stepNormal = cursorNormal;

            if (prober is not null)
            {
                stepHasGround = prober.ProbeTrajectoryStep(
                    cursorWorld,
                    stepXZ,
                    _settings.TrajectoryProbeRadiusMeters,
                    _settings.TrajectoryProbeHalfHeightMeters,
                    out Vector3 resolvedWorld,
                    out stepNormal,
                    out stepHitObstacle,
                    out Vector3 obstacleNormal);
                cursorWorld = resolvedWorld;
                cursorNormal = stepNormal;

                if (stepHitObstacle)
                {
                    hasAnyObstacle = true;
                    var wallXZ = new Vector2(obstacleNormal.X, obstacleNormal.Z);
                    float wallLenSq = wallXZ.LengthSquared();
                    if (wallLenSq > 1.0e-6f)
                    {
                        wallXZ /= MathF.Sqrt(wallLenSq);
                        var travelDir = new Vector2(MathF.Sin(simTravelYaw), MathF.Cos(simTravelYaw));
                        float intoWall = Vector2.Dot(travelDir, wallXZ);
                        if (intoWall < 0.0f)
                        {
                            Vector2 tangent = travelDir - wallXZ * intoWall;
                            float tangentLen = tangent.Length();
                            if (tangentLen > 0.35f)
                            {
                                tangent /= tangentLen;
                                simTravelYaw = MathF.Atan2(tangent.X, tangent.Y);
                                simTravelYawRate = 0.0f;
                                simSpeed *= tangentLen;
                            }
                            else
                            {
                                simSpeed = 0.0f;
                                simSpeedRate = 0.0f;
                                haltedByObstacle = true;
                            }
                        }
                    }
                    else
                    {
                        simSpeed = 0.0f;
                        simSpeedRate = 0.0f;
                        haltedByObstacle = true;
                    }
                }
            }
            else
            {
                cursorWorld += stepXZ;
            }

            _projectedTrajectory[i] = new MotionBricksTrajectoryPoint(
                cursorWorld,
                stepNormal,
                simFacingYaw,
                simTravelYaw,
                simSpeed,
                dtStep * (i + 1),
                stepHitObstacle,
                stepHasGround);
        }

        _projectedTrajectoryCount = sampleCount;
        _lastTargetGroundWorldPosition = cursorWorld;
        _lastTrajectoryHitObstacle = hasAnyObstacle;
        return sampleCount;
    }

    /// <summary>
    /// Populates placed SOMA target poses and selects an inference config for the next replan.
    /// </summary>
    public SomaInferenceConfig BuildTargetPoses(
        in MotionBricksSteeringInput input,
        Vector3 currentRootPosition,
        bool isUrgentTurnOrStop,
        Span<SomaPoseTransform> destinationTargets,
        out int targetPoseCount)
        => BuildTargetPoses(
            input,
            currentRootPosition,
            new Vector3(currentRootPosition.X, 0.0f, currentRootPosition.Z),
            avatarLegScale: 1.0f,
            prober: null,
            isUrgentTurnOrStop,
            destinationTargets,
            out targetPoseCount);

    /// <summary>
    /// Populates placed SOMA target poses from the terrain- and collision-projected future ground
    /// trajectory and selects an inference config whose token horizon matches the lookahead window.
    /// </summary>
    public SomaInferenceConfig BuildTargetPoses(
        in MotionBricksSteeringInput input,
        Vector3 currentRootPosition,
        Vector3 worldFootOrigin,
        float avatarLegScale,
        IMotionBricksTrajectoryProber? prober,
        bool isUrgentTurnOrStop,
        Span<SomaPoseTransform> destinationTargets,
        out int targetPoseCount,
        bool forceConditionTargetLocalPoses = false)
    {
        if (destinationTargets.Length < SomaModelContract.TargetPoseCapacity * SomaModelContract.JointCount)
            throw new ArgumentException("Destination target span must hold 4 poses x 30 joints.", nameof(destinationTargets));

        float legScale = float.IsFinite(avatarLegScale) && avatarLegScale > 0.01f ? avatarLegScale : 1.0f;
        int dotCount = ProjectFutureTrajectory(input, worldFootOrigin, legScale, prober, isUrgentTurnOrStop);
        MotionBricksTrajectoryPoint endPoint = dotCount > 0
            ? _projectedTrajectory[dotCount - 1]
            : new MotionBricksTrajectoryPoint(worldFootOrigin, Vector3.UnitY, _currentFacingYaw, _desiredVelocityYaw, 0.0f, _settings.LookaheadSeconds, false, false);

        ResolveSpeedBands(input, out float walkSpeed, out float jogSpeed, out float maxSpeed);
        float plannedSpeed = IsIdleLocked ? 0.0f : endPoint.SpeedMetersPerSecond * _settings.VelocityScale;
        float normalizedSpeed = MapSpeedToTemplateGait(plannedSpeed, walkSpeed, jogSpeed, maxSpeed);
        float plannedFacingYaw = endPoint.FacingYawRadians;
        float plannedTravelYaw = endPoint.TravelYawRadians;

        Vector3 worldDelta = endPoint.Position - worldFootOrigin;
        float invLegScale = 1.0f / legScale;
        Vector3 targetGround = IsIdleLocked
            ? new Vector3(currentRootPosition.X, 0.0f, currentRootPosition.Z)
            : new Vector3(
                currentRootPosition.X + worldDelta.X * invLegScale,
                Math.Clamp(worldDelta.Y * invLegScale, -0.35f, 0.35f),
                currentRootPosition.Z + worldDelta.Z * invLegScale);

        if (input.StrafeMode)
        {
            float relativeAngle = CriticalSpringDamper.WrapAngleRadians(plannedTravelYaw - plannedFacingYaw);
            float localForward = MathF.Cos(relativeAngle) * normalizedSpeed;
            float localRight = MathF.Sin(relativeAngle) * normalizedSpeed;
            _poseBank.BuildDirectional2DTarget(
                localForward,
                localRight,
                plannedSpeed,
                targetGround,
                plannedFacingYaw,
                plannedTravelYaw,
                destinationTargets,
                input.StyleIndex);
        }
        else
        {
            _poseBank.BuildLinear1DTarget(
                normalizedSpeed,
                plannedSpeed,
                targetGround,
                plannedFacingYaw,
                destinationTargets,
                input.StyleIndex);
        }

        // Apply subtle terrain height delta across all target root poses when walking slopes or steps.
        if (!IsIdleLocked && MathF.Abs(targetGround.Y) > 1.0e-4f)
        {
            for (int frame = 0; frame < SomaModelContract.TargetPoseCapacity; frame++)
            {
                int rootIdx = frame * SomaModelContract.JointCount;
                SomaPoseTransform root = destinationTargets[rootIdx];
                destinationTargets[rootIdx] = new SomaPoseTransform(
                    new Vector3(root.Position.X, MathF.Max(0.35f, root.Position.Y + targetGround.Y), root.Position.Z),
                    root.Rotation);
            }
        }

        // Idle or collision-halted targets use 1 target pose; active locomotion uses 4 target poses to carry velocity.
        bool effectivelyStopped = IsIdleLocked || _isWallStopped || plannedSpeed <= _settings.IdleLockSpeedThreshold;
        targetPoseCount = effectivelyStopped ? 1 : SomaModelContract.TargetPoseCapacity;

        // When real SOMA mocap targets are loaded, always condition target local poses (has_local_poses = 1)
        // just like NVIDIA's demo so SOMA stays anchored to clean human posture, head level, and arms.
        // Without mocap targets, condition only idle, stance changes, and instability recovery.
        bool isNonStandingStyle = input.StyleIndex != SomaLocomotionPoseBank.StandingStyleIndex;
        bool styleChanged = input.StyleIndex != _lastPlannedStyleIndex;
        bool conditionTargetLocalPoses = _poseBank.HasMocapTargets
            || effectivelyStopped
            || styleChanged
            || forceConditionTargetLocalPoses
            || (isNonStandingStyle && currentRootPosition.Y > 0.78f);

        Vector2 travelDir = new(MathF.Sin(plannedTravelYaw), MathF.Cos(plannedTravelYaw));
        _timeSinceLastReplan = 0.0f;
        _lastPlannedSpeed = effectivelyStopped ? 0.0f : MathF.Max(_desiredSpeed, plannedSpeed);
        _lastPlannedDirection = _isMovingInputActive ? _inputVelocityDirection : travelDir;
        _lastPlannedStrafeMode = input.StrafeMode;
        _lastPlannedStyleIndex = input.StyleIndex;
        _physicsReplanRequested = false;

        // Match allowed SOMA token durations (0.10 s/token, valid contract range [6, 16]) to the
        // projected horizon so stance changes (Stand <-> Crouch) respond crisply in 0.60..0.70 s,
        // urgent turns/stops resolve in 0.60..1.00 s, calm idle plays 1.20..1.40 s, and steady
        // locomotion stays smooth.
        int allowedBits = styleChanged
            ? SomaInferenceConfig.CreateAllowedTokenBits(6, 7)
            : (isUrgentTurnOrStop || _lastTrajectoryHitObstacle)
                ? SomaInferenceConfig.CreateAllowedTokenBits(6, 10)
                : effectivelyStopped
                    ? SomaInferenceConfig.CreateAllowedTokenBits(12, 14)
                    : SomaInferenceConfig.CreateAllowedTokenBits(10, 14);
        return new SomaInferenceConfig(
            allowedBits,
            correctGeneratedDiff: true,
            headingAlignment: new Vector2(1.0f, 0.0f),
            conditionTargetLocalPoses: conditionTargetLocalPoses);
    }

    private static void ResolveSpeedBands(
        in MotionBricksSteeringInput input,
        out float walkSpeed,
        out float jogSpeed,
        out float maxSpeed)
    {
        maxSpeed = MathF.Max(input.RunSpeedMetersPerSecond, input.WalkSpeedMetersPerSecond);
        walkSpeed = MathF.Min(input.WalkSpeedMetersPerSecond, maxSpeed);
        if (input.StyleIndex == SomaLocomotionPoseBank.CrouchedStyleIndex)
        {
            walkSpeed *= 0.65f;
            maxSpeed = MathF.Min(maxSpeed, walkSpeed * 1.15f);
            jogSpeed = maxSpeed;
            return;
        }

        if (input.StyleIndex == SomaLocomotionPoseBank.ProneStyleIndex)
        {
            walkSpeed *= 0.55f;
            maxSpeed = MathF.Min(maxSpeed, walkSpeed * 1.25f);
            jogSpeed = maxSpeed;
            return;
        }

        // Set the non-sprint analog stick top speed to a natural jog between walk and full SOMA run
        // so holding Shift or the gamepad left face button produces a distinct speed and gait jump.
        jogSpeed = walkSpeed + (maxSpeed - walkSpeed) * 0.35f;
    }

    /// <summary>
    /// Maps a world planar speed in meters per second to the pose bank's 1D gait parameter so
    /// walk, jog, and run speeds align with the Idle (0.0), Walk (0.45), Jog (0.72), and Run (1.0)
    /// template anchors.
    /// </summary>
    private static float MapSpeedToTemplateGait(float speed, float walkSpeed, float jogSpeed, float maxSpeed)
    {
        if (speed <= 1.0e-5f || maxSpeed <= 1.0e-5f)
            return 0.0f;
        if (speed <= walkSpeed)
            return 0.45f * Math.Clamp(speed / MathF.Max(walkSpeed, 1.0e-4f), 0.0f, 1.0f);
        if (speed <= jogSpeed)
        {
            float span = jogSpeed - walkSpeed;
            return span > 1.0e-4f
                ? 0.45f + 0.27f * Math.Clamp((speed - walkSpeed) / span, 0.0f, 1.0f)
                : 0.72f;
        }

        float runSpan = maxSpeed - jogSpeed;
        return runSpan > 1.0e-4f
            ? 0.72f + 0.28f * Math.Clamp((speed - jogSpeed) / runSpan, 0.0f, 1.0f)
            : 1.0f;
    }

    private void ResolveSteeringGoals(
        in MotionBricksSteeringInput input,
        out float maxSpeed,
        out float inputSpeed,
        out float inputYaw)
    {
        ResolveSpeedBands(input, out float walkSpeed, out float jogSpeed, out maxSpeed);
        float activeTopSpeed = input.SprintHeld ? maxSpeed : jogSpeed;

        inputSpeed = 0.0f;
        inputYaw = _desiredVelocityYaw;
        float rawMag = float.IsFinite(input.MoveInput.X) && float.IsFinite(input.MoveInput.Y)
            ? input.MoveInput.Length()
            : 0.0f;
        if (_isMovingInputActive && rawMag > 1.0e-5f)
        {
            float clampedMag = Math.Clamp(rawMag, 0.0f, 1.0f);
            Vector2 normalizedMove = input.MoveInput / rawMag;
            inputYaw = MathF.Atan2(normalizedMove.X, normalizedMove.Y);
            inputSpeed = clampedMag <= 0.6f
                ? walkSpeed * (clampedMag / 0.6f)
                : walkSpeed + (activeTopSpeed - walkSpeed) * ((clampedMag - 0.6f) / 0.4f);
        }
    }
}

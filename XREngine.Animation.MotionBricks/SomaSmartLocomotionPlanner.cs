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
    private bool _physicsReplanRequested;

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

    public Vector2 DesiredPlanarVelocity => new(
        MathF.Sin(_desiredVelocityYaw) * _desiredSpeed,
        MathF.Cos(_desiredVelocityYaw) * _desiredSpeed);

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
        _physicsReplanRequested = false;
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

        float maxSpeed = MathF.Max(input.RunSpeedMetersPerSecond, input.WalkSpeedMetersPerSecond);
        float walkSpeed = MathF.Min(input.WalkSpeedMetersPerSecond, maxSpeed);
        float inputSpeed = 0.0f;
        float inputYaw = _desiredVelocityYaw;

        if (_isMovingInputActive && rawMag > 1.0e-5f)
        {
            float clampedMag = Math.Clamp(rawMag, 0.0f, 1.0f);
            Vector2 normalizedMove = input.MoveInput / rawMag;
            _inputVelocityDirection = normalizedMove;
            inputYaw = MathF.Atan2(normalizedMove.X, normalizedMove.Y);

            // 0..0.6 maps smoothly up to walk speed; 0.6..1.0 maps walk -> run speed.
            inputSpeed = clampedMag <= 0.6f
                ? walkSpeed * (clampedMag / 0.6f)
                : walkSpeed + (maxSpeed - walkSpeed) * ((clampedMag - 0.6f) / 0.4f);
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
        if (_isMovingInputActive)
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
        if (!_isMovingInputActive && _desiredSpeed < _settings.IdleLockSpeedThreshold)
        {
            _desiredSpeed = 0.0f;
            _desiredSpeedRate = 0.0f;
        }
    }

    /// <summary>
    /// Applies feedback from Jolt collision blocking so the planner slows or slides along walls
    /// and schedules an early replan.
    /// </summary>
    public void NotifyPhysicsCollisionClamp(Vector3 actualPlanarVelocity, Vector3 wallNormal, bool isBlocked)
    {
        if (!isBlocked)
            return;

        float actualSpeed = MathF.Sqrt(
            actualPlanarVelocity.X * actualPlanarVelocity.X
            + actualPlanarVelocity.Z * actualPlanarVelocity.Z);
        if (actualSpeed < _desiredSpeed)
            _desiredSpeed = actualSpeed;

        // Project desired travel direction onto the wall tangent when colliding obliquely.
        var normalXZ = new Vector2(wallNormal.X, wallNormal.Z);
        float normalLenSq = normalXZ.LengthSquared();
        if (normalLenSq > 1.0e-6f)
        {
            normalXZ /= MathF.Sqrt(normalLenSq);
            var desiredDir = new Vector2(MathF.Sin(_desiredVelocityYaw), MathF.Cos(_desiredVelocityYaw));
            float intoWall = Vector2.Dot(desiredDir, normalXZ);
            if (intoWall < 0.0f)
            {
                Vector2 tangent = desiredDir - normalXZ * intoWall;
                if (tangent.LengthSquared() > 1.0e-4f)
                {
                    tangent = Vector2.Normalize(tangent);
                    _desiredVelocityYaw = MathF.Atan2(tangent.X, tangent.Y);
                    _desiredVelocityYawRate = 0.0f;
                }
            }
        }

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

        if (_timeSinceLastReplan < _settings.MinimumReplanCooldownSeconds)
            return false;

        if (input.StrafeMode != _lastPlannedStrafeMode)
        {
            isUrgentTurnOrStop = true;
            return true;
        }

        float maxSpeed = MathF.Max(input.RunSpeedMetersPerSecond, 0.1f);
        float speedDelta = MathF.Abs(_desiredSpeed - _lastPlannedSpeed);
        if (speedDelta >= MathF.Max(input.InputReplanThreshold, maxSpeed * _settings.SpeedRegenerationFraction))
        {
            isUrgentTurnOrStop = true;
            return true;
        }

        if (_isMovingInputActive)
        {
            float directionDot = Vector2.Dot(_inputVelocityDirection, _lastPlannedDirection);
            if (directionDot < _settings.DirectionRegenerationDot)
            {
                isUrgentTurnOrStop = true;
                return true;
            }
        }

        return _timeSinceLastReplan >= input.ReplanIntervalSeconds;
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
    {
        if (destinationTargets.Length < SomaModelContract.TargetPoseCapacity * SomaModelContract.JointCount)
            throw new ArgumentException("Destination target span must hold 4 poses x 30 joints.", nameof(destinationTargets));

        float maxSpeed = MathF.Max(input.RunSpeedMetersPerSecond, 0.1f);
        float plannedSpeed = IsIdleLocked ? 0.0f : _desiredSpeed * _settings.VelocityScale;
        float normalizedSpeed = Math.Clamp(_desiredSpeed / maxSpeed, 0.0f, 1.0f);

        Vector2 travelDir = new(MathF.Sin(_desiredVelocityYaw), MathF.Cos(_desiredVelocityYaw));
        float horizonSeconds = isUrgentTurnOrStop ? _settings.LookaheadSeconds * 0.65f : _settings.LookaheadSeconds;
        Vector3 targetGround = IsIdleLocked
            ? new Vector3(currentRootPosition.X, 0.0f, currentRootPosition.Z)
            : new Vector3(
                currentRootPosition.X + travelDir.X * plannedSpeed * horizonSeconds * _settings.PositionScale,
                0.0f,
                currentRootPosition.Z + travelDir.Y * plannedSpeed * horizonSeconds * _settings.PositionScale);

        if (input.StrafeMode)
        {
            float relativeAngle = CriticalSpringDamper.WrapAngleRadians(_desiredVelocityYaw - _currentFacingYaw);
            float localForward = MathF.Cos(relativeAngle) * normalizedSpeed;
            float localRight = MathF.Sin(relativeAngle) * normalizedSpeed;
            _poseBank.BuildDirectional2DTarget(
                localForward,
                localRight,
                plannedSpeed,
                targetGround,
                _currentFacingYaw,
                _desiredVelocityYaw,
                destinationTargets);
        }
        else
        {
            _poseBank.BuildLinear1DTarget(
                normalizedSpeed,
                plannedSpeed,
                targetGround,
                _currentFacingYaw,
                destinationTargets);
        }

        // Idle uses a single target pose; active locomotion supplies 4 target poses to carry velocity.
        targetPoseCount = IsIdleLocked ? 1 : SomaModelContract.TargetPoseCapacity;

        _timeSinceLastReplan = 0.0f;
        _lastPlannedSpeed = _desiredSpeed;
        _lastPlannedDirection = _isMovingInputActive ? _inputVelocityDirection : travelDir;
        _lastPlannedStrafeMode = input.StrafeMode;
        _physicsReplanRequested = false;

        int allowedBits = isUrgentTurnOrStop
            ? SomaInferenceConfig.CreateAllowedTokenBits(6, 10)
            : SomaInferenceConfig.DefaultAllowedTokenBits;
        return new SomaInferenceConfig(
            allowedBits,
            correctGeneratedDiff: true,
            headingAlignment: new Vector2(1.0f, 0.0f));
    }
}

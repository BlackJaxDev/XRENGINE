using System;
using System.Collections.Generic;
using System.Numerics;
using MemoryPack;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;

namespace XREngine.Animation;

/// <summary>
/// Allocation-free procedural foot-stepping locomotion <see cref="MotionBase"/> that drives
/// humanoid muscles and root motion from steering parameters with zero mocap clips and zero GPU dependencies.
/// Supports walking, running, strafing, slow micro-step shuffles, turn-in-place pivoting, and stance styles.
/// </summary>
[MemoryPackable]
public partial class ProceduralStrideMotion : MotionBase, IProceduralLocomotionMotion
{
    private const float DefaultStandingHipHeight = 0.92f;
    private const float DefaultCrouchedHipHeight = 0.64f;
    private const float DefaultProneHipHeight = 0.24f;
    private const float DefaultLegLength = 0.88f;

    private string _moveXParameter = "MoveX";
    private string _moveYParameter = "MoveY";
    private string _desiredFacingYawParameter = "DesiredFacingYaw";
    private string _strafeModeParameter = "StrafeMode";
    private string _styleParameter = "LocomotionStyle";
    private string _isGroundedParameter = "IsGrounded";
    private float _walkSpeedMetersPerSecond = 1.55f;
    private float _runSpeedMetersPerSecond = 3.40f;
    private float _strideLengthMeters = 0.42f;
    private float _stepHeightMeters = 0.13f;
    private float _stepDurationSeconds = 0.28f;
    private float _footStanceWidthMeters = 0.22f;
    private float _turnStepYawThresholdDegrees = 26.0f;
    private float _microStepDistanceMeters = 0.08f;

    [MemoryPackIgnore]
    private readonly HumanoidMuscleSlotBinding _slotBinding = new(MotionBricksLocomotionMotion.DrivenMuscleChannels);

    [MemoryPackIgnore]
    private readonly float[] _evaluatedMuscles = new float[MotionBricksEvaluatedPose.MuscleChannelCount];

    [MemoryPackIgnore]
    private readonly object _driverToken = new();

    [MemoryPackIgnore]
    private IMotionBricksTrajectoryProber? _trajectoryProber;

    [MemoryPackIgnore]
    private float _worldRootMotionScale = 1.0f;

    [MemoryPackIgnore]
    private Vector3 _canonicalBodyPosition = new(0.0f, DefaultStandingHipHeight, 0.0f);

    [MemoryPackIgnore]
    private Quaternion _canonicalBodyRotation = Quaternion.Identity;

    [MemoryPackIgnore]
    private Vector3 _currentBodyPosition = new(0.0f, DefaultStandingHipHeight, 0.0f);

    [MemoryPackIgnore]
    private float _currentBodyYaw;

    [MemoryPackIgnore]
    private Vector2 _currentVelocityXZ;

    [MemoryPackIgnore]
    private float _currentHipHeight = DefaultStandingHipHeight;

    [MemoryPackIgnore]
    private Vector3 _leftFootWorld;

    [MemoryPackIgnore]
    private Vector3 _rightFootWorld;

    [MemoryPackIgnore]
    private Vector3 _leftStepStart;

    [MemoryPackIgnore]
    private Vector3 _leftStepTarget;

    [MemoryPackIgnore]
    private Vector3 _rightStepStart;

    [MemoryPackIgnore]
    private Vector3 _rightStepTarget;

    [MemoryPackIgnore]
    private float _leftFootYaw;

    [MemoryPackIgnore]
    private float _rightFootYaw;

    [MemoryPackIgnore]
    private float _leftStepProgress;

    [MemoryPackIgnore]
    private float _rightStepProgress;

    [MemoryPackIgnore]
    private bool _leftStepping;

    [MemoryPackIgnore]
    private bool _rightStepping;

    [MemoryPackIgnore]
    private bool _preferLeftNext = true;

    [MemoryPackIgnore]
    private bool _feetInitialized;

    [MemoryPackIgnore]
    private bool _hasValidPose;

    [MemoryPackIgnore]
    private float _pendingDeltaSeconds;

    [MemoryPackIgnore]
    private bool _hasPendingTick;

    [MemoryPackIgnore]
    private int _leftStepCount;

    [MemoryPackIgnore]
    private int _rightStepCount;

    public string MoveXParameter
    {
        get => _moveXParameter;
        set => SetField(ref _moveXParameter, value ?? string.Empty);
    }

    public string MoveYParameter
    {
        get => _moveYParameter;
        set => SetField(ref _moveYParameter, value ?? string.Empty);
    }

    public string DesiredFacingYawParameter
    {
        get => _desiredFacingYawParameter;
        set => SetField(ref _desiredFacingYawParameter, value ?? string.Empty);
    }

    public string StrafeModeParameter
    {
        get => _strafeModeParameter;
        set => SetField(ref _strafeModeParameter, value ?? string.Empty);
    }

    public string StyleParameter
    {
        get => _styleParameter;
        set => SetField(ref _styleParameter, value ?? string.Empty);
    }

    public string IsGroundedParameter
    {
        get => _isGroundedParameter;
        set => SetField(ref _isGroundedParameter, value ?? string.Empty);
    }

    public float WalkSpeedMetersPerSecond
    {
        get => _walkSpeedMetersPerSecond;
        set => SetField(ref _walkSpeedMetersPerSecond, float.IsFinite(value) ? Math.Clamp(value, 0.1f, 8.0f) : 1.55f);
    }

    public float RunSpeedMetersPerSecond
    {
        get => _runSpeedMetersPerSecond;
        set => SetField(ref _runSpeedMetersPerSecond, float.IsFinite(value) ? Math.Clamp(value, _walkSpeedMetersPerSecond, 15.0f) : 3.40f);
    }

    public float StrideLengthMeters
    {
        get => _strideLengthMeters;
        set => SetField(ref _strideLengthMeters, float.IsFinite(value) ? Math.Clamp(value, 0.12f, 1.2f) : 0.42f);
    }

    public float StepHeightMeters
    {
        get => _stepHeightMeters;
        set => SetField(ref _stepHeightMeters, float.IsFinite(value) ? Math.Clamp(value, 0.02f, 0.45f) : 0.13f);
    }

    public float StepDurationSeconds
    {
        get => _stepDurationSeconds;
        set => SetField(ref _stepDurationSeconds, float.IsFinite(value) ? Math.Clamp(value, 0.10f, 0.80f) : 0.28f);
    }

    public float FootStanceWidthMeters
    {
        get => _footStanceWidthMeters;
        set => SetField(ref _footStanceWidthMeters, float.IsFinite(value) ? Math.Clamp(value, 0.10f, 0.50f) : 0.22f);
    }

    public float TurnStepYawThresholdDegrees
    {
        get => _turnStepYawThresholdDegrees;
        set => SetField(ref _turnStepYawThresholdDegrees, float.IsFinite(value) ? Math.Clamp(value, 8.0f, 75.0f) : 26.0f);
    }

    public float MicroStepDistanceMeters
    {
        get => _microStepDistanceMeters;
        set => SetField(ref _microStepDistanceMeters, float.IsFinite(value) ? Math.Clamp(value, 0.03f, 0.30f) : 0.08f);
    }

    [MemoryPackIgnore]
    public int LeftStepCount => _leftStepCount;

    [MemoryPackIgnore]
    public int RightStepCount => _rightStepCount;

    [MemoryPackIgnore]
    public bool IsLeftFootStepping => _leftStepping;

    [MemoryPackIgnore]
    public bool IsRightFootStepping => _rightStepping;

    [MemoryPackIgnore]
    public Vector3 LeftFootWorldPosition => _leftFootWorld;

    [MemoryPackIgnore]
    public Vector3 RightFootWorldPosition => _rightFootWorld;

    [MemoryPackIgnore]
    public Vector3 CurrentBodyPosition => _currentBodyPosition;

    [MemoryPackIgnore]
    public float CurrentBodyYawRadians => _currentBodyYaw;

    [MemoryPackIgnore]
    public object? RuntimeDriverToken => _driverToken;

    internal override bool RequiresHumanoidAvatar => true;
    internal override bool IsProceduralMotion => true;

    internal override AnimationMember EnsureProceduralBindingRootMember()
        => _slotBinding.EnsureBindingRootMember();

    internal override int PrepareProceduralHumanoidContributionCapacity() => 1;

    internal override void OnProceduralSlotLayoutBound(
        AnimationSlotLayout layout,
        IReadOnlyDictionary<string, AnimSlot> slotsByPath)
        => _slotBinding.CacheResolvedSlots();

    internal override void OnProceduralTickDelta(float deltaSeconds)
    {
        if (float.IsFinite(deltaSeconds) && deltaSeconds > 0.0f)
            _pendingDeltaSeconds += deltaSeconds;
        _hasPendingTick = true;
    }

    internal override void RestartProceduralPlayback(float startSeconds)
    {
        _pendingDeltaSeconds = 0.0f;
        _hasPendingTick = false;
        _currentVelocityXZ = Vector2.Zero;
        _leftStepping = false;
        _rightStepping = false;
        _leftStepProgress = 0.0f;
        _rightStepProgress = 0.0f;
        _leftStepCount = 0;
        _rightStepCount = 0;
        _feetInitialized = false;
        _hasValidPose = false;
        Array.Clear(_evaluatedMuscles);
    }

    internal override void StopProceduralPlayback()
    {
        _pendingDeltaSeconds = 0.0f;
        _hasPendingTick = false;
    }

    public override void Tick(float delta)
        => OnProceduralTickDelta(delta);

    public override void GetAnimationValues(MotionBase? parentMotion, IDictionary<string, AnimVar> variables, float weight)
    {
        EvaluateProceduralMotionValues(variables, 0.0, additive: false);
        if (parentMotion is not null && SlotLayout is not null && !ReferenceEquals(parentMotion, this))
            parentMotion.ValueStore.CopyFrom(ValueStore);
    }

    public void ConfigureLocomotionRuntime(
        float initialWorldYawRadians,
        float worldRootMotionScale,
        IMotionBricksTrajectoryProber? trajectoryProber)
    {
        if (float.IsFinite(initialWorldYawRadians))
        {
            _currentBodyYaw = WrapAngleRadians(initialWorldYawRadians);
            _canonicalBodyRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _currentBodyYaw);
        }

        if (float.IsFinite(worldRootMotionScale) && worldRootMotionScale > 0.0f)
            _worldRootMotionScale = worldRootMotionScale;

        _trajectoryProber = trajectoryProber;
        _feetInitialized = false;
    }

    public void DetachTrajectoryProber()
        => _trajectoryProber = null;

    public void ApplyPhysicsRootReconciliation(
        Vector3 resolvedFootPosition,
        Vector3 actualPlanarVelocity,
        Vector3 wallNormal,
        bool isBlocked)
    {
        if (!IsFinite(resolvedFootPosition))
            return;

        _currentBodyPosition = new Vector3(
            resolvedFootPosition.X,
            resolvedFootPosition.Y + _currentHipHeight,
            resolvedFootPosition.Z);

        if (isBlocked && IsFinite(wallNormal))
        {
            var normalXZ = new Vector2(wallNormal.X, wallNormal.Z);
            float lenSq = normalXZ.LengthSquared();
            if (lenSq > 1.0e-6f)
            {
                normalXZ /= MathF.Sqrt(lenSq);
                float intoWall = Vector2.Dot(_currentVelocityXZ, normalXZ);
                if (intoWall < 0.0f)
                    _currentVelocityXZ -= normalXZ * intoWall;
            }
        }
        else if (IsFinite(actualPlanarVelocity))
        {
            _currentVelocityXZ = Vector2.Lerp(_currentVelocityXZ, new Vector2(actualPlanarVelocity.X, actualPlanarVelocity.Z), 0.35f);
        }
    }

    public bool TryGetCanonicalBodyRotation(out Quaternion canonicalBodyRotation)
    {
        if (_hasValidPose)
        {
            canonicalBodyRotation = _canonicalBodyRotation;
            return true;
        }

        canonicalBodyRotation = Quaternion.Identity;
        return false;
    }

    public bool TryGetCurrentBodyRotation(out Quaternion currentBodyRotation)
    {
        if (_hasValidPose)
        {
            currentBodyRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _currentBodyYaw);
            return true;
        }

        currentBodyRotation = Quaternion.Identity;
        return false;
    }

    public bool TryGetBodySamples(
        out Vector3 canonicalBodyPosition,
        out Quaternion canonicalBodyRotation,
        out Vector3 currentBodyPosition,
        out Quaternion currentBodyRotation)
    {
        if (!_hasValidPose)
        {
            canonicalBodyPosition = Vector3.Zero;
            canonicalBodyRotation = Quaternion.Identity;
            currentBodyPosition = Vector3.Zero;
            currentBodyRotation = Quaternion.Identity;
            return false;
        }

        canonicalBodyPosition = _canonicalBodyPosition;
        canonicalBodyRotation = _canonicalBodyRotation;
        currentBodyPosition = _currentBodyPosition;
        currentBodyRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _currentBodyYaw);
        return true;
    }

    public void CopyProjectionMuscles(Span<float> destination)
    {
        int count = Math.Min(_evaluatedMuscles.Length, destination.Length);
        _evaluatedMuscles.AsSpan(0, count).CopyTo(destination);
    }

    internal override bool TryCreateProceduralHumanoidContribution(
        double normalizedTime,
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        out HumanoidMotionContribution contribution)
    {
        if (!_hasValidPose || !float.IsFinite(weight) || weight <= 0.0f)
        {
            contribution = default;
            return false;
        }

        contribution = new HumanoidMotionContribution(
            Clip: null,
            Policy: MotionBricksLocomotionMotion.DefaultRootMotionPolicy,
            OccurrenceId: occurrenceId,
            LifecycleGeneration: lifecycleGeneration,
            Weight: weight,
            SampleTime: 0.0f,
            SamplePhase: 0.0f,
            PlaybackLoopCycle: 0L,
            SourceLoopCycle: 0L,
            ContributionType: EHumanoidMotionContributionType.Override,
            ProceduralSource: this);
        return true;
    }

    internal override void EvaluateProceduralMotionValues(
        IDictionary<string, AnimVar> variables,
        double normalizedTime,
        bool additive)
    {
        if (_slotBinding.NeedsSlotRefresh)
            _slotBinding.CacheResolvedSlots();

        float dt = _hasPendingTick ? Math.Clamp(_pendingDeltaSeconds, 0.0f, 0.1f) : 0.0f;
        _pendingDeltaSeconds = 0.0f;
        _hasPendingTick = false;

        var moveInput = new Vector2(
            ReadVariableFloat(variables, _moveXParameter, 0.0f),
            ReadVariableFloat(variables, _moveYParameter, 0.0f));
        float inputMag = Math.Clamp(moveInput.Length(), 0.0f, 1.0f);
        float desiredFacingYaw = WrapAngleRadians(ReadVariableFloat(variables, _desiredFacingYawParameter, _currentBodyYaw));
        bool strafeMode = ReadVariableBool(variables, _strafeModeParameter, false);
        var style = (ELocomotionStyle)Math.Clamp(ReadVariableInt(variables, _styleParameter, 0), 0, 2);
        bool isGrounded = ReadVariableBool(variables, _isGroundedParameter, true);

        float speedScale = style switch
        {
            ELocomotionStyle.Crouched => 0.62f,
            ELocomotionStyle.Prone => 0.32f,
            _ => 1.0f,
        };
        float desiredSpeed = inputMag <= 0.65f
            ? (inputMag / 0.65f) * _walkSpeedMetersPerSecond * speedScale
            : (_walkSpeedMetersPerSecond + ((inputMag - 0.65f) / 0.35f) * (_runSpeedMetersPerSecond - _walkSpeedMetersPerSecond)) * speedScale;

        Vector2 desiredVelocityXZ = inputMag > 1.0e-4f
            ? (moveInput / inputMag) * desiredSpeed
            : Vector2.Zero;

        float accelAlpha = dt > 0.0f ? 1.0f - MathF.Exp(-10.0f * dt) : 1.0f;
        _currentVelocityXZ = Vector2.Lerp(_currentVelocityXZ, desiredVelocityXZ, accelAlpha);
        float planarSpeed = _currentVelocityXZ.Length();

        // Determine desired body facing yaw.
        float targetYaw = _currentBodyYaw;
        if (strafeMode || inputMag <= 1.0e-4f)
            targetYaw = desiredFacingYaw;
        else if (planarSpeed > 0.05f)
            targetYaw = MathF.Atan2(_currentVelocityXZ.X, _currentVelocityXZ.Y);

        float yawDelta = WrapAngleRadians(targetYaw - _currentBodyYaw);
        if (dt > 0.0f)
        {
            float yawRate = 1.0f - MathF.Exp(-8.5f * dt);
            _currentBodyYaw = WrapAngleRadians(_currentBodyYaw + yawDelta * yawRate);
        }

        // Advance planar body root and stance height.
        float groundY = _currentBodyPosition.Y - _currentHipHeight;
        float targetHipHeight = (style switch
        {
            ELocomotionStyle.Crouched => DefaultCrouchedHipHeight,
            ELocomotionStyle.Prone => DefaultProneHipHeight,
            _ => DefaultStandingHipHeight,
        }) * _worldRootMotionScale;
        _currentHipHeight += (targetHipHeight - _currentHipHeight) * (dt > 0.0f ? 1.0f - MathF.Exp(-10.0f * dt) : 1.0f);

        if (isGrounded && dt > 0.0f)
        {
            _currentBodyPosition.X += _currentVelocityXZ.X * dt;
            _currentBodyPosition.Z += _currentVelocityXZ.Y * dt;
            if (_trajectoryProber is not null
                && _trajectoryProber.ProbeGroundHeight(
                    new Vector3(_currentBodyPosition.X, groundY, _currentBodyPosition.Z),
                    0.5f,
                    1.5f,
                    out float probedY,
                    out _))
            {
                groundY = probedY;
            }
        }

        _currentBodyPosition.Y = groundY + _currentHipHeight;

        Vector3 right = new(MathF.Cos(_currentBodyYaw), 0.0f, -MathF.Sin(_currentBodyYaw));
        float halfWidth = _footStanceWidthMeters * 0.5f * _worldRootMotionScale;

        Vector3 idealLeft = new Vector3(_currentBodyPosition.X, groundY, _currentBodyPosition.Z) - right * halfWidth;
        Vector3 idealRight = new Vector3(_currentBodyPosition.X, groundY, _currentBodyPosition.Z) + right * halfWidth;

        if (!_feetInitialized)
        {
            _leftFootWorld = idealLeft;
            _rightFootWorld = idealRight;
            _leftFootYaw = _currentBodyYaw;
            _rightFootYaw = _currentBodyYaw;
            _canonicalBodyPosition = _currentBodyPosition;
            _canonicalBodyRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _currentBodyYaw);
            _feetInitialized = true;
        }

        UpdateFootStepping(dt, isGrounded, planarSpeed, groundY, right, halfWidth, idealLeft, idealRight);
        SynthesizeMuscles(style, planarSpeed, groundY, right, halfWidth);

        _hasValidPose = true;

        EHumanoidValue[] channels = MotionBricksLocomotionMotion.DrivenMuscleChannels;
        for (int i = 0; i < channels.Length; i++)
            _slotBinding.WriteChannel(ValueStore, i, _evaluatedMuscles[(int)channels[i]]);
    }

    private void UpdateFootStepping(
        float dt,
        bool isGrounded,
        float planarSpeed,
        float groundY,
        Vector3 right,
        float halfWidth,
        Vector3 idealLeft,
        Vector3 idealRight)
    {
        if (!isGrounded)
        {
            _leftStepping = false;
            _rightStepping = false;
            _leftFootWorld = Vector3.Lerp(_leftFootWorld, idealLeft + new Vector3(0.0f, 0.12f, 0.0f), dt > 0.0f ? 1.0f - MathF.Exp(-8.0f * dt) : 1.0f);
            _rightFootWorld = Vector3.Lerp(_rightFootWorld, idealRight + new Vector3(0.0f, 0.06f, 0.0f), dt > 0.0f ? 1.0f - MathF.Exp(-8.0f * dt) : 1.0f);
            return;
        }

        float effectiveDuration = Math.Clamp(
            _stepDurationSeconds / MathF.Max(0.65f, MathF.Min(1.75f, planarSpeed / MathF.Max(0.5f, _walkSpeedMetersPerSecond))),
            0.14f,
            _stepDurationSeconds);

        // Advance active swing arc.
        if (_leftStepping)
        {
            _leftStepProgress = MathF.Min(1.0f, _leftStepProgress + (dt > 0.0f ? dt / effectiveDuration : 1.0f));
            _leftFootWorld = EvaluateStepArc(_leftStepStart, _leftStepTarget, _leftStepProgress, _stepHeightMeters);
            _leftFootYaw = _currentBodyYaw;
            if (_leftStepProgress >= 1.0f)
            {
                _leftFootWorld = _leftStepTarget;
                _leftStepping = false;
                _preferLeftNext = false;
            }
        }

        if (_rightStepping)
        {
            _rightStepProgress = MathF.Min(1.0f, _rightStepProgress + (dt > 0.0f ? dt / effectiveDuration : 1.0f));
            _rightFootWorld = EvaluateStepArc(_rightStepStart, _rightStepTarget, _rightStepProgress, _stepHeightMeters);
            _rightFootYaw = _currentBodyYaw;
            if (_rightStepProgress >= 1.0f)
            {
                _rightFootWorld = _rightStepTarget;
                _rightStepping = false;
                _preferLeftNext = true;
            }
        }

        if (_leftStepping || _rightStepping)
            return;

        // Dynamic stride trigger threshold shrinks for slow shuffles so micro-steps still animate cleanly.
        float speedRatio = Math.Clamp(planarSpeed / MathF.Max(0.2f, _walkSpeedMetersPerSecond), 0.0f, 1.5f);
        float distThreshold = (_microStepDistanceMeters + (_strideLengthMeters * 0.55f - _microStepDistanceMeters) * MathF.Min(1.0f, speedRatio)) * _worldRootMotionScale;
        float yawThresholdRad = _turnStepYawThresholdDegrees * (MathF.PI / 180.0f);

        float leftDist = HorizontalDistance(_leftFootWorld, idealLeft);
        float rightDist = HorizontalDistance(_rightFootWorld, idealRight);
        float leftYawErr = MathF.Abs(WrapAngleRadians(_currentBodyYaw - _leftFootYaw));
        float rightYawErr = MathF.Abs(WrapAngleRadians(_currentBodyYaw - _rightFootYaw));

        bool leftNeedsStep = leftDist >= distThreshold || leftYawErr >= yawThresholdRad;
        bool rightNeedsStep = rightDist >= distThreshold || rightYawErr >= yawThresholdRad;
        if (!leftNeedsStep && !rightNeedsStep)
            return;

        bool stepLeft = leftNeedsStep && rightNeedsStep
            ? (_preferLeftNext ? leftDist >= rightDist * 0.85f : leftDist > rightDist * 1.15f)
            : leftNeedsStep;

        float leadSeconds = effectiveDuration * 0.55f;
        var leadOffset = new Vector3(_currentVelocityXZ.X * leadSeconds, 0.0f, _currentVelocityXZ.Y * leadSeconds);

        if (stepLeft)
        {
            Vector3 target = idealLeft + leadOffset;
            // Prevent left foot from crossing past the right side of the body midline.
            Vector3 relToCenter = target - new Vector3(_currentBodyPosition.X, groundY, _currentBodyPosition.Z);
            float lateral = Vector3.Dot(relToCenter, right);
            if (lateral > -halfWidth * 0.45f)
                target -= right * (lateral + halfWidth * 0.45f);

            target.Y = ProbeFootY(target, groundY);
            _leftStepStart = _leftFootWorld;
            _leftStepTarget = target;
            _leftStepProgress = 0.0f;
            _leftStepping = true;
            _leftStepCount++;
        }
        else
        {
            Vector3 target = idealRight + leadOffset;
            // Prevent right foot from crossing past the left side of the body midline.
            Vector3 relToCenter = target - new Vector3(_currentBodyPosition.X, groundY, _currentBodyPosition.Z);
            float lateral = Vector3.Dot(relToCenter, right);
            if (lateral < halfWidth * 0.45f)
                target += right * (halfWidth * 0.45f - lateral);

            target.Y = ProbeFootY(target, groundY);
            _rightStepStart = _rightFootWorld;
            _rightStepTarget = target;
            _rightStepProgress = 0.0f;
            _rightStepping = true;
            _rightStepCount++;
        }
    }

    private float ProbeFootY(Vector3 worldPos, float fallbackY)
    {
        if (_trajectoryProber is not null
            && _trajectoryProber.ProbeGroundHeight(worldPos, 0.45f, 1.25f, out float hitY, out _))
        {
            return hitY;
        }

        return fallbackY;
    }

    private void SynthesizeMuscles(
        ELocomotionStyle style,
        float planarSpeed,
        float groundY,
        Vector3 right,
        float halfWidth)
    {
        Array.Clear(_evaluatedMuscles);

        Quaternion invBodyYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -_currentBodyYaw);
        float legLength = DefaultLegLength * _worldRootMotionScale;

        Vector3 leftHipWorld = _currentBodyPosition - right * halfWidth;
        Vector3 rightHipWorld = _currentBodyPosition + right * halfWidth;

        Vector3 leftLocal = Vector3.Transform(_leftFootWorld - leftHipWorld, invBodyYaw);
        Vector3 rightLocal = Vector3.Transform(_rightFootWorld - rightHipWorld, invBodyYaw);

        SolveLegMuscles(
            leftLocal,
            legLength,
            style,
            isLeft: true,
            out float lHipFB,
            out float lHipInOut,
            out float lKnee,
            out float lFoot);
        SolveLegMuscles(
            rightLocal,
            legLength,
            style,
            isLeft: false,
            out float rHipFB,
            out float rHipInOut,
            out float rKnee,
            out float rFoot);

        _evaluatedMuscles[(int)EHumanoidValue.LeftUpperLegFrontBack] = lHipFB;
        _evaluatedMuscles[(int)EHumanoidValue.LeftUpperLegInOut] = lHipInOut;
        _evaluatedMuscles[(int)EHumanoidValue.LeftLowerLegStretch] = lKnee;
        _evaluatedMuscles[(int)EHumanoidValue.LeftFootUpDown] = lFoot;

        _evaluatedMuscles[(int)EHumanoidValue.RightUpperLegFrontBack] = rHipFB;
        _evaluatedMuscles[(int)EHumanoidValue.RightUpperLegInOut] = rHipInOut;
        _evaluatedMuscles[(int)EHumanoidValue.RightLowerLegStretch] = rKnee;
        _evaluatedMuscles[(int)EHumanoidValue.RightFootUpDown] = rFoot;

        // Pelvis bob, torso forward lean, counter-twist, and opposite arm swing.
        float strideDiff = Math.Clamp((leftLocal.Z - rightLocal.Z) / MathF.Max(0.25f, legLength), -1.0f, 1.0f);
        float speedFactor = Math.Clamp(planarSpeed / MathF.Max(0.2f, _runSpeedMetersPerSecond), 0.0f, 1.0f);
        float stancePitch = style switch
        {
            ELocomotionStyle.Crouched => -0.35f,
            ELocomotionStyle.Prone => -0.78f,
            _ => -0.10f * speedFactor,
        };

        _evaluatedMuscles[(int)EHumanoidValue.SpineFrontBack] = stancePitch;
        _evaluatedMuscles[(int)EHumanoidValue.ChestFrontBack] = stancePitch * 0.45f;
        _evaluatedMuscles[(int)EHumanoidValue.SpineTwistLeftRight] = Math.Clamp(-strideDiff * 0.30f, -0.5f, 0.5f);
        _evaluatedMuscles[(int)EHumanoidValue.SpineLeftRight] = Math.Clamp((_leftStepping ? 0.06f : _rightStepping ? -0.06f : 0.0f), -0.3f, 0.3f);

        float armSwing = Math.Clamp(strideDiff * (0.45f + 0.40f * speedFactor), -0.85f, 0.85f);
        float armDown = style == ELocomotionStyle.Prone ? 0.35f : -0.55f;
        float elbowBend = style == ELocomotionStyle.Prone ? -0.55f : (-0.20f - 0.40f * speedFactor);

        _evaluatedMuscles[(int)EHumanoidValue.LeftArmDownUp] = armDown;
        _evaluatedMuscles[(int)EHumanoidValue.RightArmDownUp] = armDown;
        _evaluatedMuscles[(int)EHumanoidValue.LeftArmFrontBack] = -armSwing;
        _evaluatedMuscles[(int)EHumanoidValue.RightArmFrontBack] = armSwing;
        _evaluatedMuscles[(int)EHumanoidValue.LeftForearmStretch] = elbowBend;
        _evaluatedMuscles[(int)EHumanoidValue.RightForearmStretch] = elbowBend;

        // Subtle head level compensation in crouch/prone.
        _evaluatedMuscles[(int)EHumanoidValue.NeckNodDownUp] = Math.Clamp(-stancePitch * 0.45f, -0.5f, 0.7f);
        _evaluatedMuscles[(int)EHumanoidValue.HeadNodDownUp] = Math.Clamp(-stancePitch * 0.45f, -0.5f, 0.7f);
        _ = groundY;
    }

    private static void SolveLegMuscles(
        Vector3 localFootFromHip,
        float legLength,
        ELocomotionStyle style,
        bool isLeft,
        out float upperLegFrontBack,
        out float upperLegInOut,
        out float lowerLegStretch,
        out float footUpDown)
    {
        float upperLen = legLength * 0.52f;
        float lowerLen = legLength * 0.48f;
        float dist = Math.Clamp(localFootFromHip.Length(), 0.18f * legLength, legLength * 0.995f);

        // Two-bone law-of-cosines knee bend angle (0 = straight, pi = folded).
        float cosKnee = Math.Clamp(
            (upperLen * upperLen + lowerLen * lowerLen - dist * dist) / MathF.Max(1.0e-5f, 2.0f * upperLen * lowerLen),
            -1.0f,
            1.0f);
        float kneeBendRad = MathF.PI - MathF.Acos(cosKnee);

        float cosHipOffset = Math.Clamp(
            (upperLen * upperLen + dist * dist - lowerLen * lowerLen) / MathF.Max(1.0e-5f, 2.0f * upperLen * dist),
            -1.0f,
            1.0f);
        float hipOffsetRad = MathF.Acos(cosHipOffset);

        float baseSwingRad = MathF.Atan2(localFootFromHip.Z, MathF.Max(0.10f, -localFootFromHip.Y));
        float totalHipPitchRad = baseSwingRad + hipOffsetRad;
        float lateralRad = MathF.Atan2(isLeft ? -localFootFromHip.X : localFootFromHip.X, MathF.Max(0.15f, -localFootFromHip.Y));

        if (style == ELocomotionStyle.Prone)
        {
            upperLegFrontBack = Math.Clamp(-0.15f + (localFootFromHip.Z / MathF.Max(0.25f, legLength)) * 0.45f, -0.8f, 0.8f);
            upperLegInOut = Math.Clamp(lateralRad / 0.8f, -0.6f, 0.6f);
            lowerLegStretch = Math.Clamp(-0.45f - kneeBendRad / MathF.PI * 0.45f, -1.0f, 0.0f);
            footUpDown = -0.35f;
            return;
        }

        // Normalize angles into canonical Unity/XRENGINE humanoid muscle range [-1, 1].
        upperLegFrontBack = Math.Clamp(totalHipPitchRad / 1.35f, -1.0f, 1.0f);
        upperLegInOut = Math.Clamp(lateralRad / 0.90f, -1.0f, 1.0f);
        // LowerLegStretch: +1 = straight, -1 = fully bent.
        lowerLegStretch = Math.Clamp(1.0f - (kneeBendRad / 2.35f) * 2.0f, -1.0f, 1.0f);
        footUpDown = Math.Clamp(-(totalHipPitchRad - kneeBendRad) / 0.90f, -0.8f, 0.8f);
    }

    private static Vector3 EvaluateStepArc(Vector3 start, Vector3 target, float t, float stepHeight)
    {
        float smooth = t * t * (3.0f - 2.0f * t);
        Vector3 pos = Vector3.Lerp(start, target, smooth);
        pos.Y += MathF.Sin(t * MathF.PI) * stepHeight;
        return pos;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float WrapAngleRadians(float radians)
    {
        if (!float.IsFinite(radians))
            return 0.0f;

        radians = (radians + MathF.PI) % MathF.Tau;
        if (radians < 0.0f)
            radians += MathF.Tau;
        return radians - MathF.PI;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

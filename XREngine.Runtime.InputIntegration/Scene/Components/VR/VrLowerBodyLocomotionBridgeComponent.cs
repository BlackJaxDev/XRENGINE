using System;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

/// <summary>
/// Bridges 3-point VR tracking (headset + hands, with optional pelvis tracker) into
/// lower-body procedural locomotion (<see cref="SteeringSnapshot"/>) so any
/// <c>IProceduralLocomotionMotion</c> synthesizes natural hips and legs while VR IK
/// retains uncontested ownership of the upper body.
/// </summary>
[Category("VR")]
[DisplayName("VR Lower-Body Locomotion Bridge")]
[Description("Derives planar root velocity, facing yaw, and crouch stance from calibrated VR head/pelvis tracking and publishes SteeringSnapshot.")]
public sealed class VrLowerBodyLocomotionBridgeComponent : XRComponent
{
    private TransformBase? _headTransform;
    private TransformBase? _pelvisTransform;
    private CharacterMovement3DComponent? _characterMovement;
    private float _calibratedStandingHeadHeight = 1.65f;
    private float _crouchHeightRatio = 0.78f;
    private float _proneHeightRatio = 0.42f;
    private float _maxNormalizedSpeedMetersPerSecond = 3.2f;
    private float _velocitySmoothingSpeed = 12.0f;
    private bool _strafeMode = true;

    private Vector3 _previousRootWorldPos;
    private Vector2 _smoothedVelocityXZ;
    private bool _hasPreviousPosition;

    /// <summary>
    /// Raised each update with a view-agnostic <see cref="SteeringSnapshot"/> derived from
    /// VR head and pelvis movement. Bindable in the Inspector to
    /// <c>CharacterRootMotionBridgeComponent.ApplySteeringSnapshot</c>.
    /// </summary>
    public XREvent<SteeringSnapshot>? SteeringUpdated;

    public TransformBase? HeadTransform
    {
        get => _headTransform;
        set => SetField(ref _headTransform, value);
    }

    public TransformBase? PelvisTransform
    {
        get => _pelvisTransform;
        set => SetField(ref _pelvisTransform, value);
    }

    public CharacterMovement3DComponent? CharacterMovement
    {
        get => _characterMovement;
        set => SetField(ref _characterMovement, value);
    }

    public float CalibratedStandingHeadHeight
    {
        get => _calibratedStandingHeadHeight;
        set => SetField(ref _calibratedStandingHeadHeight, float.IsFinite(value) ? Math.Clamp(value, 0.8f, 2.5f) : 1.65f);
    }

    public float CrouchHeightRatio
    {
        get => _crouchHeightRatio;
        set => SetField(ref _crouchHeightRatio, float.IsFinite(value) ? Math.Clamp(value, 0.45f, 0.92f) : 0.78f);
    }

    public float ProneHeightRatio
    {
        get => _proneHeightRatio;
        set => SetField(ref _proneHeightRatio, float.IsFinite(value) ? Math.Clamp(value, 0.15f, _crouchHeightRatio - 0.05f) : 0.42f);
    }

    public float MaxNormalizedSpeedMetersPerSecond
    {
        get => _maxNormalizedSpeedMetersPerSecond;
        set => SetField(ref _maxNormalizedSpeedMetersPerSecond, float.IsFinite(value) ? Math.Clamp(value, 0.5f, 10.0f) : 3.2f);
    }

    public float VelocitySmoothingSpeed
    {
        get => _velocitySmoothingSpeed;
        set => SetField(ref _velocitySmoothingSpeed, float.IsFinite(value) ? MathF.Max(0.5f, value) : 12.0f);
    }

    public bool StrafeMode
    {
        get => _strafeMode;
        set => SetField(ref _strafeMode, value);
    }

    public SteeringSnapshot LastPublishedSteering { get; private set; }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        ResetTrackingState();
        RegisterTick(ETickGroup.Normal, (int)ETickOrder.Input + 1, TickVrSteering);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, (int)ETickOrder.Input + 1, TickVrSteering);
        ResetTrackingState();
        base.OnComponentDeactivated();
    }

    public void ResetTrackingState()
    {
        _hasPreviousPosition = false;
        _smoothedVelocityXZ = Vector2.Zero;
    }

    private void TickVrSteering()
    {
        float dt = RuntimeTransformServices.Current?.DilatedUpdateDeltaSeconds ?? 0.0f;
        StepVrSteering(dt);
    }

    /// <summary>
    /// Computes one <see cref="SteeringSnapshot"/> from the tracked head/pelvis transforms
    /// and invokes <see cref="SteeringUpdated"/> without allocating.
    /// </summary>
    public SteeringSnapshot StepVrSteering(float deltaSeconds)
    {
        TransformBase? primary = _pelvisTransform ?? _headTransform ?? SceneNode?.Transform;
        TransformBase? head = _headTransform ?? primary;
        if (primary is null || head is null)
            return LastPublishedSteering;

        Vector3 currentPos = primary.WorldTranslation;
        if (_hasPreviousPosition && float.IsFinite(deltaSeconds) && deltaSeconds > 1.0e-5f)
        {
            var rawVelXZ = new Vector2(
                (currentPos.X - _previousRootWorldPos.X) / deltaSeconds,
                (currentPos.Z - _previousRootWorldPos.Z) / deltaSeconds);
            float alpha = 1.0f - MathF.Exp(-_velocitySmoothingSpeed * deltaSeconds);
            _smoothedVelocityXZ = Vector2.Lerp(_smoothedVelocityXZ, rawVelXZ, alpha);
        }
        else
        {
            _smoothedVelocityXZ = Vector2.Zero;
        }

        _previousRootWorldPos = currentPos;
        _hasPreviousPosition = true;

        Vector2 desiredMoveXZ = _smoothedVelocityXZ / MathF.Max(0.25f, _maxNormalizedSpeedMetersPerSecond);
        float magSq = desiredMoveXZ.LengthSquared();
        if (magSq > 1.0f)
            desiredMoveXZ /= MathF.Sqrt(magSq);
        else if (magSq < 1.0e-4f)
            desiredMoveXZ = Vector2.Zero;

        // Facing yaw prefers pelvis forward when a waist tracker is bound, falling back to HMD forward.
        Vector3 facingForward = primary.WorldForward;
        float fwdXZSq = facingForward.X * facingForward.X + facingForward.Z * facingForward.Z;
        float facingYaw = fwdXZSq > 1.0e-6f ? MathF.Atan2(facingForward.X, facingForward.Z) : 0.0f;

        // Derive stance from head height relative to calibrated standing height.
        float floorY = SceneNode?.Transform.WorldTranslation.Y ?? 0.0f;
        float headHeight = head.WorldTranslation.Y - floorY;
        float heightRatio = _calibratedStandingHeadHeight > 0.1f
            ? headHeight / _calibratedStandingHeadHeight
            : 1.0f;

        ELocomotionStyle style = heightRatio <= _proneHeightRatio
            ? ELocomotionStyle.Prone
            : heightRatio <= _crouchHeightRatio
                ? ELocomotionStyle.Crouched
                : ELocomotionStyle.Standing;

        CharacterMovement3DComponent? movement = _characterMovement ?? SceneNode?.GetComponent<CharacterMovement3DComponent>();
        bool isGrounded = movement?.CharacterController?.IsGrounded ?? true;
        float verticalVelocity = movement?.CharacterController?.LinearVelocity.Y ?? 0.0f;
        bool sprintHeld = desiredMoveXZ.LengthSquared() > 0.72f * 0.72f;

        var snapshot = new SteeringSnapshot(
            desiredMoveXZ,
            facingYaw,
            _strafeMode,
            sprintHeld,
            isGrounded,
            style,
            float.IsFinite(verticalVelocity) ? verticalVelocity : 0.0f);

        LastPublishedSteering = snapshot;
        SteeringUpdated?.Invoke(snapshot);
        return snapshot;
    }
}

using System;
using System.ComponentModel;
using System.Numerics;
using XREngine.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Computes avatar-local look-at yaw and pitch angles toward a world <see cref="TargetTransform"/>
/// (or explicit <see cref="TargetWorldPosition"/>) and writes them into an
/// <see cref="AnimStateMachineComponent"/> for <see cref="HumanoidLookAtAimMotion"/>.
/// </summary>
[Category("Animation")]
[DisplayName("Humanoid Look Target Driver")]
[Description("Converts a world target transform or position into avatar-local AimYaw and AimPitch state-machine parameters.")]
public sealed class HumanoidLookTargetDriverComponent : XRComponent
{
    private AnimStateMachineComponent? _animator;
    private TransformBase? _avatarFacingTransform;
    private TransformBase? _headReferenceTransform;
    private TransformBase? _targetTransform;
    private Vector3 _targetWorldPosition;
    private bool _useExplicitWorldPosition;
    private string _aimYawParameter = "AimYaw";
    private string _aimPitchParameter = "AimPitch";
    private string _aimWeightParameter = "AimWeight";
    private float _maxPitchDegrees = 65.0f;
    private float _maxEffectiveYawDegrees = 135.0f;
    private float _weight = 1.0f;
    private float _targetSwitchSmoothingSpeed = 12.0f;

    private float _currentYawRadians;
    private float _currentPitchRadians;
    private float _currentWeight;
    private bool _initialized;

    public AnimStateMachineComponent? Animator
    {
        get => _animator;
        set => SetField(ref _animator, value);
    }

    public TransformBase? AvatarFacingTransform
    {
        get => _avatarFacingTransform;
        set => SetField(ref _avatarFacingTransform, value);
    }

    public TransformBase? HeadReferenceTransform
    {
        get => _headReferenceTransform;
        set => SetField(ref _headReferenceTransform, value);
    }

    public TransformBase? TargetTransform
    {
        get => _targetTransform;
        set => SetField(ref _targetTransform, value);
    }

    public Vector3 TargetWorldPosition
    {
        get => _targetWorldPosition;
        set => SetField(ref _targetWorldPosition, value);
    }

    public bool UseExplicitWorldPosition
    {
        get => _useExplicitWorldPosition;
        set => SetField(ref _useExplicitWorldPosition, value);
    }

    public string AimYawParameter
    {
        get => _aimYawParameter;
        set => SetField(ref _aimYawParameter, value ?? string.Empty);
    }

    public string AimPitchParameter
    {
        get => _aimPitchParameter;
        set => SetField(ref _aimPitchParameter, value ?? string.Empty);
    }

    public string AimWeightParameter
    {
        get => _aimWeightParameter;
        set => SetField(ref _aimWeightParameter, value ?? string.Empty);
    }

    public float MaxPitchDegrees
    {
        get => _maxPitchDegrees;
        set => SetField(ref _maxPitchDegrees, float.IsFinite(value) ? Math.Clamp(value, 5.0f, 89.0f) : 65.0f);
    }

    public float MaxEffectiveYawDegrees
    {
        get => _maxEffectiveYawDegrees;
        set => SetField(ref _maxEffectiveYawDegrees, float.IsFinite(value) ? Math.Clamp(value, 15.0f, 180.0f) : 135.0f);
    }

    public float Weight
    {
        get => _weight;
        set => SetField(ref _weight, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 1.0f);
    }

    public float TargetSwitchSmoothingSpeed
    {
        get => _targetSwitchSmoothingSpeed;
        set => SetField(ref _targetSwitchSmoothingSpeed, float.IsFinite(value) ? MathF.Max(0.1f, value) : 12.0f);
    }

    public float LastAimYawRadians => _currentYawRadians;
    public float LastAimPitchRadians => _currentPitchRadians;
    public float LastAimWeight => _currentWeight;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _initialized = false;
        RegisterTick(ETickGroup.Normal, (int)ETickOrder.Animation - 1, TickLookTarget);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, (int)ETickOrder.Animation - 1, TickLookTarget);
        base.OnComponentDeactivated();
    }

    private void TickLookTarget()
    {
        float dt = RuntimeTransformServices.Current?.DilatedUpdateDeltaSeconds ?? 0.0f;
        StepTargetUpdate(dt);
    }

    /// <summary>
    /// Evaluates the look target relative to the avatar and writes smoothed yaw, pitch, and
    /// weight parameters into the bound <see cref="AnimStateMachineComponent"/>.
    /// </summary>
    public void StepTargetUpdate(float deltaSeconds)
    {
        AnimStateMachineComponent? animator = _animator
            ?? (SceneNode?.GetComponentInHierarchy(nameof(AnimStateMachineComponent)) as AnimStateMachineComponent);
        if (animator is null)
            return;

        TransformBase? facing = _avatarFacingTransform ?? SceneNode?.Transform;
        TransformBase? originTransform = _headReferenceTransform ?? facing;
        if (facing is null || originTransform is null)
            return;

        bool hasTarget = _useExplicitWorldPosition || _targetTransform is not null;
        float desiredYaw = 0.0f;
        float desiredPitch = 0.0f;
        float desiredWeight = 0.0f;

        if (hasTarget)
        {
            Vector3 targetPos = _useExplicitWorldPosition ? _targetWorldPosition : _targetTransform!.WorldTranslation;
            Vector3 toTarget = targetPos - originTransform.WorldTranslation;
            if (toTarget.LengthSquared() > 1.0e-6f)
            {
                Quaternion facingRot = facing.WorldRotation;
                Quaternion invFacing = facingRot.LengthSquared() > 1.0e-8f
                    ? Quaternion.Conjugate(Quaternion.Normalize(facingRot))
                    : Quaternion.Identity;
                Vector3 localDir = Vector3.Normalize(Vector3.Transform(toTarget, invFacing));

                float rawYaw = WrapAngleRadians(MathF.Atan2(localDir.X, localDir.Z));
                float horizLen = MathF.Sqrt(localDir.X * localDir.X + localDir.Z * localDir.Z);
                float maxPitchRad = _maxPitchDegrees * (MathF.PI / 180.0f);
                float rawPitch = Math.Clamp(MathF.Atan2(localDir.Y, horizLen), -maxPitchRad, maxPitchRad);

                float maxYawRad = _maxEffectiveYawDegrees * (MathF.PI / 180.0f);
                float absYaw = MathF.Abs(rawYaw);
                float yawAttenuation = absYaw <= maxYawRad
                    ? 1.0f
                    : Math.Clamp(1.0f - (absYaw - maxYawRad) / MathF.Max(0.15f, MathF.PI - maxYawRad), 0.0f, 1.0f);

                desiredYaw = rawYaw;
                desiredPitch = rawPitch;
                desiredWeight = _weight * yawAttenuation;
            }
        }

        if (!_initialized || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0.0f)
        {
            _currentYawRadians = desiredYaw;
            _currentPitchRadians = desiredPitch;
            _currentWeight = desiredWeight;
            _initialized = true;
        }
        else
        {
            float alpha = 1.0f - MathF.Exp(-_targetSwitchSmoothingSpeed * deltaSeconds);
            float yawStep = WrapAngleRadians(desiredYaw - _currentYawRadians);
            _currentYawRadians = WrapAngleRadians(_currentYawRadians + yawStep * alpha);
            _currentPitchRadians += (desiredPitch - _currentPitchRadians) * alpha;
            _currentWeight += (desiredWeight - _currentWeight) * alpha;
        }

        if (!string.IsNullOrEmpty(_aimYawParameter))
            animator.SetFloat(_aimYawParameter, _currentYawRadians);
        if (!string.IsNullOrEmpty(_aimPitchParameter))
            animator.SetFloat(_aimPitchParameter, _currentPitchRadians);
        if (!string.IsNullOrEmpty(_aimWeightParameter))
            animator.SetFloat(_aimWeightParameter, _currentWeight);
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
}

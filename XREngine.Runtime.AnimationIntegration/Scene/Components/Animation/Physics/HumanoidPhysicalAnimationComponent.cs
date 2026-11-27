using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Drives humanoid physics rigid bodies toward the animated skeleton pose using
/// proportional-derivative (PD) linear and angular springs keyed by <see cref="EHumanoidBodyMask"/>.
/// Supports localized hit reactions, active ragdoll compliance, full ragdoll entry, and
/// smooth get-up recovery with zero per-frame allocations.
/// </summary>
[Category("Physics")]
[DisplayName("Humanoid Physical Animation")]
[Description("Applies PD spring drives toward animated humanoid bones, enabling hit reactions, active ragdoll compliance, and ragdoll get-up recovery.")]
public sealed class HumanoidPhysicalAnimationComponent : XRComponent
{
    private readonly List<HumanoidPhysicalBoneBinding> _bindings = [];

    private HumanoidComponent? _humanoid;
    private List<HumanoidPhysicalRegionProfile> _regionProfiles = CreateDefaultProfiles();
    private float _masterBlendWeight = 1.0f;
    private bool _isRagdoll;
    private bool _writePhysicsPoseToBones = true;
    private Vector3 _defaultHitImpulse = new(0.0f, 0.5f, -4.5f);
    private float _defaultHitComplianceSeconds = 0.45f;
    private float _defaultHitComplianceScale = 0.20f;

    private bool _isRecoveringFromRagdoll;
    private float _recoveryElapsedSeconds;
    private float _recoveryDurationSeconds = 0.65f;
    private float _recoveryStartWeight;

    public HumanoidComponent? Humanoid
    {
        get => _humanoid;
        set => SetField(ref _humanoid, value);
    }

    public List<HumanoidPhysicalRegionProfile> RegionProfiles
    {
        get => _regionProfiles;
        set => SetField(ref _regionProfiles, value ?? CreateDefaultProfiles());
    }

    public float MasterBlendWeight
    {
        get => _masterBlendWeight;
        set => SetField(ref _masterBlendWeight, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 1.0f);
    }

    public bool IsRagdoll
    {
        get => _isRagdoll;
        set => SetField(ref _isRagdoll, value);
    }

    public bool WritePhysicsPoseToBones
    {
        get => _writePhysicsPoseToBones;
        set => SetField(ref _writePhysicsPoseToBones, value);
    }

    public Vector3 DefaultHitImpulse
    {
        get => _defaultHitImpulse;
        set => SetField(ref _defaultHitImpulse, value);
    }

    public float DefaultHitComplianceSeconds
    {
        get => _defaultHitComplianceSeconds;
        set => SetField(ref _defaultHitComplianceSeconds, float.IsFinite(value) ? Math.Clamp(value, 0.05f, 3.0f) : 0.45f);
    }

    public float DefaultHitComplianceScale
    {
        get => _defaultHitComplianceScale;
        set => SetField(ref _defaultHitComplianceScale, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.20f);
    }

    public int BoundBodyCount => _bindings.Count;
    public bool IsRecoveringFromRagdoll => _isRecoveringFromRagdoll;

    /// <summary>Registers a bone transform and its dynamic rigid body for PD physical animation.</summary>
    public void BindDynamicBody(
        string name,
        EHumanoidBodyMask region,
        Transform boneTransform,
        IAbstractDynamicRigidBody rigidBody)
    {
        ArgumentNullException.ThrowIfNull(boneTransform);
        ArgumentNullException.ThrowIfNull(rigidBody);

        for (int i = 0; i < _bindings.Count; i++)
        {
            if (ReferenceEquals(_bindings[i].BoneTransform, boneTransform))
            {
                _bindings[i] = new HumanoidPhysicalBoneBinding(name, region, boneTransform, rigidBody);
                return;
            }
        }

        _bindings.Add(new HumanoidPhysicalBoneBinding(name, region, boneTransform, rigidBody));
    }

    /// <summary>Removes all dynamic rigid body bindings without destroying the bodies.</summary>
    public void ClearBindings()
        => _bindings.Clear();

    /// <summary>
    /// Applies a localized hit impulse to every bound body in <paramref name="region"/> and
    /// temporarily reduces that region's PD spring strength so it deflects naturally and recovers.
    /// </summary>
    public void TriggerHitReaction(
        EHumanoidBodyMask region,
        Vector3 worldLinearImpulse,
        Vector3 worldAngularImpulse = default,
        float complianceDurationSeconds = 0.45f,
        float complianceWeightScale = 0.20f)
    {
        float duration = float.IsFinite(complianceDurationSeconds) ? Math.Clamp(complianceDurationSeconds, 0.05f, 5.0f) : 0.45f;
        float scale = float.IsFinite(complianceWeightScale) ? Math.Clamp(complianceWeightScale, 0.0f, 1.0f) : 0.20f;

        for (int i = 0; i < _bindings.Count; i++)
        {
            HumanoidPhysicalBoneBinding binding = _bindings[i];
            if ((binding.Region & region) == 0)
                continue;

            IAbstractDynamicRigidBody body = binding.RigidBody;
            body.WakeUp();
            if (IsFinite(worldLinearImpulse) && worldLinearImpulse.LengthSquared() > 1.0e-8f)
                body.SetLinearVelocity(body.LinearVelocity + worldLinearImpulse, wake: true);
            if (IsFinite(worldAngularImpulse) && worldAngularImpulse.LengthSquared() > 1.0e-8f)
                body.SetAngularVelocity(body.AngularVelocity + worldAngularImpulse, wake: true);

            binding.HitComplianceTotalSeconds = duration;
            binding.HitComplianceRemainingSeconds = duration;
            binding.HitComplianceWeightScale = scale;
        }
    }

    /// <summary>
    /// Zero-parameter Inspector <c>XREvent</c> trigger that applies <see cref="DefaultHitImpulse"/>
    /// to the upper body (<see cref="EHumanoidBodyMask.UpperBody"/>).
    /// </summary>
    public void TriggerUpperBodyHitReaction()
        => TriggerHitReaction(
            EHumanoidBodyMask.UpperBody,
            _defaultHitImpulse,
            Vector3.Zero,
            _defaultHitComplianceSeconds,
            _defaultHitComplianceScale);

    /// <summary>Drops PD tracking to zero and releases all bound rigid bodies to full gravity ragdoll.</summary>
    public void EnterRagdoll()
    {
        IsRagdoll = true;
        _isRecoveringFromRagdoll = false;
        MasterBlendWeight = 0.0f;

        for (int i = 0; i < _bindings.Count; i++)
        {
            IAbstractDynamicRigidBody body = _bindings[i].RigidBody;
            body.GravityEnabled = true;
            body.WakeUp();
        }
    }

    /// <summary>Exits full ragdoll and smoothly ramps PD tracking back to 1.0 over <paramref name="durationSeconds"/>.</summary>
    public void BeginGetUpRecovery(float durationSeconds = 0.65f)
    {
        IsRagdoll = false;
        _isRecoveringFromRagdoll = true;
        _recoveryElapsedSeconds = 0.0f;
        _recoveryDurationSeconds = float.IsFinite(durationSeconds) ? Math.Clamp(durationSeconds, 0.05f, 5.0f) : 0.65f;
        _recoveryStartWeight = _masterBlendWeight;
    }

    /// <summary>Zero-parameter Inspector <c>XREvent</c> hook to start get-up recovery with default timing.</summary>
    public void TriggerGetUpRecovery()
        => BeginGetUpRecovery();

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.PrePhysics, (int)ETickOrder.Scene, TickPhysicalAnimation);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.PrePhysics, (int)ETickOrder.Scene, TickPhysicalAnimation);
        base.OnComponentDeactivated();
    }

    private void TickPhysicalAnimation()
    {
        float dt = RuntimeTransformServices.Current?.DilatedUpdateDeltaSeconds ?? (1.0f / 60.0f);
        StepPhysicalAnimation(dt);
    }

    /// <summary>
    /// Advances hit-reaction compliance timers, get-up recovery blending, and PD spring velocity
    /// drives for all bound rigid bodies with zero heap allocations.
    /// </summary>
    public void StepPhysicalAnimation(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0.0f || _bindings.Count == 0)
            return;

        float dt = Math.Clamp(deltaSeconds, 1.0e-4f, 0.05f);

        if (_isRecoveringFromRagdoll)
        {
            _recoveryElapsedSeconds += dt;
            float t = Math.Clamp(_recoveryElapsedSeconds / _recoveryDurationSeconds, 0.0f, 1.0f);
            float smooth = t * t * (3.0f - 2.0f * t);
            MasterBlendWeight = _recoveryStartWeight + (1.0f - _recoveryStartWeight) * smooth;
            if (t >= 1.0f)
                _isRecoveringFromRagdoll = false;
        }

        for (int i = 0; i < _bindings.Count; i++)
        {
            HumanoidPhysicalBoneBinding binding = _bindings[i];
            float hitFactor = 1.0f;
            if (binding.HitComplianceRemainingSeconds > 0.0f)
            {
                binding.HitComplianceRemainingSeconds = MathF.Max(0.0f, binding.HitComplianceRemainingSeconds - dt);
                float remainRatio = binding.HitComplianceTotalSeconds > 1.0e-5f
                    ? binding.HitComplianceRemainingSeconds / binding.HitComplianceTotalSeconds
                    : 0.0f;
                hitFactor = 1.0f - (1.0f - binding.HitComplianceWeightScale) * remainRatio;
            }

            HumanoidPhysicalRegionProfile profile = FindRegionProfile(binding.Region);
            float effectiveWeight = _isRagdoll
                ? 0.0f
                : Math.Clamp(_masterBlendWeight * profile.ClampedBlendWeight * hitFactor, 0.0f, 1.0f);

            IAbstractDynamicRigidBody body = binding.RigidBody;
            (Vector3 bodyPos, Quaternion bodyRot) = body.Transform;

            if (effectiveWeight > 1.0e-4f)
            {
                Vector3 targetPos = binding.BoneTransform.WorldTranslation;
                Quaternion targetRot = binding.BoneTransform.WorldRotation;
                if (targetRot.LengthSquared() > 1.0e-8f)
                    targetRot = Quaternion.Normalize(targetRot);
                else
                    targetRot = Quaternion.Identity;

                if (bodyRot.LengthSquared() > 1.0e-8f)
                    bodyRot = Quaternion.Normalize(bodyRot);
                else
                    bodyRot = Quaternion.Identity;

                // PD linear spring drive.
                Vector3 posError = targetPos - bodyPos;
                Vector3 linAccel = (profile.LinearStiffness * posError - profile.LinearDamping * body.LinearVelocity) * effectiveWeight;
                Vector3 nextLinVel = body.LinearVelocity + linAccel * dt;
                float linSpeedSq = nextLinVel.LengthSquared();
                float maxLin = MathF.Max(0.1f, profile.MaxLinearSpeed);
                if (linSpeedSq > maxLin * maxLin)
                    nextLinVel *= maxLin / MathF.Sqrt(linSpeedSq);
                body.SetLinearVelocity(nextLinVel, wake: true);

                // PD angular spring drive via shortest-arc quaternion axis-angle error.
                Quaternion rotError = targetRot * Quaternion.Conjugate(bodyRot);
                if (rotError.W < 0.0f)
                    rotError = new Quaternion(-rotError.X, -rotError.Y, -rotError.Z, -rotError.W);

                var errVec = new Vector3(rotError.X, rotError.Y, rotError.Z);
                float sinHalf = errVec.Length();
                Vector3 angError = Vector3.Zero;
                if (sinHalf > 1.0e-6f)
                {
                    float angle = 2.0f * MathF.Atan2(sinHalf, Math.Clamp(rotError.W, 0.0f, 1.0f));
                    angError = (errVec / sinHalf) * angle;
                }

                Vector3 angAccel = (profile.AngularStiffness * angError - profile.AngularDamping * body.AngularVelocity) * effectiveWeight;
                Vector3 nextAngVel = body.AngularVelocity + angAccel * dt;
                float angSpeedSq = nextAngVel.LengthSquared();
                float maxAng = MathF.Max(0.1f, profile.MaxAngularSpeed);
                if (angSpeedSq > maxAng * maxAng)
                    nextAngVel *= maxAng / MathF.Sqrt(angSpeedSq);
                body.SetAngularVelocity(nextAngVel, wake: true);
            }

            if (_writePhysicsPoseToBones)
            {
                float physicsPoseBlend = _isRagdoll ? 1.0f : (1.0f - effectiveWeight * 0.5f);
                Vector3 blendedPos = Vector3.Lerp(binding.BoneTransform.WorldTranslation, bodyPos, physicsPoseBlend);
                Quaternion blendedRot = Quaternion.Slerp(
                    binding.BoneTransform.WorldRotation.LengthSquared() > 1.0e-8f
                        ? Quaternion.Normalize(binding.BoneTransform.WorldRotation)
                        : Quaternion.Identity,
                    bodyRot.LengthSquared() > 1.0e-8f ? Quaternion.Normalize(bodyRot) : Quaternion.Identity,
                    physicsPoseBlend);
                binding.BoneTransform.SetWorldTranslationRotation(blendedPos, blendedRot);
            }
        }
    }

    private HumanoidPhysicalRegionProfile FindRegionProfile(EHumanoidBodyMask region)
    {
        for (int i = 0; i < _regionProfiles.Count; i++)
        {
            if ((_regionProfiles[i].Region & region) != 0)
                return _regionProfiles[i];
        }

        return FallbackProfile;
    }

    private static readonly HumanoidPhysicalRegionProfile FallbackProfile = new();

    private static List<HumanoidPhysicalRegionProfile> CreateDefaultProfiles()
        =>
        [
            new HumanoidPhysicalRegionProfile { Region = EHumanoidBodyMask.Root, LinearStiffness = 240.0f, LinearDamping = 22.0f, AngularStiffness = 180.0f, AngularDamping = 18.0f },
            new HumanoidPhysicalRegionProfile { Region = EHumanoidBodyMask.Body, LinearStiffness = 190.0f, LinearDamping = 19.0f, AngularStiffness = 150.0f, AngularDamping = 16.0f },
            new HumanoidPhysicalRegionProfile { Region = EHumanoidBodyMask.Head, LinearStiffness = 150.0f, LinearDamping = 16.0f, AngularStiffness = 120.0f, AngularDamping = 14.0f },
            new HumanoidPhysicalRegionProfile { Region = EHumanoidBodyMask.LeftArm | EHumanoidBodyMask.RightArm, LinearStiffness = 130.0f, LinearDamping = 14.0f, AngularStiffness = 100.0f, AngularDamping = 12.0f },
            new HumanoidPhysicalRegionProfile { Region = EHumanoidBodyMask.LeftLeg | EHumanoidBodyMask.RightLeg, LinearStiffness = 220.0f, LinearDamping = 20.0f, AngularStiffness = 170.0f, AngularDamping = 17.0f },
        ];

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

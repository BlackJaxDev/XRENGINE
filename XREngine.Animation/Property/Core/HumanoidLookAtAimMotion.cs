using System;
using System.Collections.Generic;
using MemoryPack;
using XREngine.Components.Animation;

namespace XREngine.Animation;

/// <summary>
/// Procedural <see cref="MotionBase"/> for additive or upper-body override layers that
/// distributes smoothed yaw and pitch look-at / aim offsets across spine, chest, upper chest,
/// neck, head, and optional arms without contesting eye gaze channels.
/// </summary>
[MemoryPackable]
public partial class HumanoidLookAtAimMotion : MotionBase
{
    private const int SpinePitchIndex = 0;
    private const int SpineYawIndex = 1;
    private const int ChestPitchIndex = 2;
    private const int ChestYawIndex = 3;
    private const int UpperChestPitchIndex = 4;
    private const int UpperChestYawIndex = 5;
    private const int NeckPitchIndex = 6;
    private const int NeckYawIndex = 7;
    private const int HeadPitchIndex = 8;
    private const int HeadYawIndex = 9;
    private const int LeftShoulderPitchIndex = 10;
    private const int LeftArmPitchIndex = 11;
    private const int LeftArmYawIndex = 12;
    private const int RightShoulderPitchIndex = 13;
    private const int RightArmPitchIndex = 14;
    private const int RightArmYawIndex = 15;

    public static readonly EHumanoidValue[] DrivenMuscleChannels =
    [
        EHumanoidValue.SpineFrontBack,
        EHumanoidValue.SpineTwistLeftRight,
        EHumanoidValue.ChestFrontBack,
        EHumanoidValue.ChestTwistLeftRight,
        EHumanoidValue.UpperChestFrontBack,
        EHumanoidValue.UpperChestTwistLeftRight,
        EHumanoidValue.NeckNodDownUp,
        EHumanoidValue.NeckTurnLeftRight,
        EHumanoidValue.HeadNodDownUp,
        EHumanoidValue.HeadTurnLeftRight,
        EHumanoidValue.LeftShoulderDownUp,
        EHumanoidValue.LeftArmDownUp,
        EHumanoidValue.LeftArmFrontBack,
        EHumanoidValue.RightShoulderDownUp,
        EHumanoidValue.RightArmDownUp,
        EHumanoidValue.RightArmFrontBack,
    ];

    private string _aimYawParameter = "AimYaw";
    private string _aimPitchParameter = "AimPitch";
    private string _aimWeightParameter = "AimWeight";
    private string _armAimWeightParameter = "ArmAimWeight";
    private float _maxYawDegrees = 95.0f;
    private float _maxPitchDegrees = 65.0f;
    private float _spineShare = 0.25f;
    private float _chestShare = 0.30f;
    private float _neckShare = 0.20f;
    private float _headShare = 0.25f;
    private float _armAimScale = 0.45f;
    private float _springFrequencyHz = 7.0f;

    [MemoryPackIgnore]
    private readonly HumanoidMuscleSlotBinding _slotBinding = new(DrivenMuscleChannels);

    [MemoryPackIgnore]
    private float _smoothedYaw;

    [MemoryPackIgnore]
    private float _yawVelocity;

    [MemoryPackIgnore]
    private float _smoothedPitch;

    [MemoryPackIgnore]
    private float _pitchVelocity;

    [MemoryPackIgnore]
    private float _pendingDeltaSeconds;

    [MemoryPackIgnore]
    private bool _hasPendingTick;

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

    public string ArmAimWeightParameter
    {
        get => _armAimWeightParameter;
        set => SetField(ref _armAimWeightParameter, value ?? string.Empty);
    }

    public float MaxYawDegrees
    {
        get => _maxYawDegrees;
        set => SetField(ref _maxYawDegrees, float.IsFinite(value) ? Math.Clamp(value, 5.0f, 175.0f) : 95.0f);
    }

    public float MaxPitchDegrees
    {
        get => _maxPitchDegrees;
        set => SetField(ref _maxPitchDegrees, float.IsFinite(value) ? Math.Clamp(value, 5.0f, 89.0f) : 65.0f);
    }

    public float SpineShare
    {
        get => _spineShare;
        set => SetField(ref _spineShare, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.25f);
    }

    public float ChestShare
    {
        get => _chestShare;
        set => SetField(ref _chestShare, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.30f);
    }

    public float NeckShare
    {
        get => _neckShare;
        set => SetField(ref _neckShare, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.20f);
    }

    public float HeadShare
    {
        get => _headShare;
        set => SetField(ref _headShare, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.25f);
    }

    public float ArmAimScale
    {
        get => _armAimScale;
        set => SetField(ref _armAimScale, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.45f);
    }

    public float SpringFrequencyHz
    {
        get => _springFrequencyHz;
        set => SetField(ref _springFrequencyHz, float.IsFinite(value) ? Math.Clamp(value, 0.5f, 30.0f) : 7.0f);
    }

    [MemoryPackIgnore]
    public float CurrentSmoothedYawRadians => _smoothedYaw;

    [MemoryPackIgnore]
    public float CurrentSmoothedPitchRadians => _smoothedPitch;

    internal override bool RequiresHumanoidAvatar => true;
    internal override bool IsProceduralMotion => true;

    internal override AnimationMember EnsureProceduralBindingRootMember()
        => _slotBinding.EnsureBindingRootMember();

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
        _smoothedYaw = 0.0f;
        _yawVelocity = 0.0f;
        _smoothedPitch = 0.0f;
        _pitchVelocity = 0.0f;
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

        float maxYawRad = _maxYawDegrees * (MathF.PI / 180.0f);
        float maxPitchRad = _maxPitchDegrees * (MathF.PI / 180.0f);
        float targetYaw = Math.Clamp(ReadVariableFloat(variables, _aimYawParameter, 0.0f), -maxYawRad, maxYawRad);
        float targetPitch = Math.Clamp(ReadVariableFloat(variables, _aimPitchParameter, 0.0f), -maxPitchRad, maxPitchRad);
        float aimWeight = Math.Clamp(ReadVariableFloat(variables, _aimWeightParameter, 1.0f), 0.0f, 1.0f);
        float armAimWeight = Math.Clamp(ReadVariableFloat(variables, _armAimWeightParameter, 0.0f), 0.0f, 1.0f);

        if (dt > 1.0e-5f)
        {
            StepCriticallyDampedSpring(ref _smoothedYaw, ref _yawVelocity, targetYaw, _springFrequencyHz, dt);
            StepCriticallyDampedSpring(ref _smoothedPitch, ref _pitchVelocity, targetPitch, _springFrequencyHz, dt);
        }
        else
        {
            _smoothedYaw = targetYaw;
            _smoothedPitch = targetPitch;
            _yawVelocity = 0.0f;
            _pitchVelocity = 0.0f;
        }

        float normYaw = maxYawRad > 1.0e-4f ? Math.Clamp(_smoothedYaw / maxYawRad, -1.0f, 1.0f) * aimWeight : 0.0f;
        float normPitch = maxPitchRad > 1.0e-4f ? Math.Clamp(_smoothedPitch / maxPitchRad, -1.0f, 1.0f) * aimWeight : 0.0f;

        float totalShare = _spineShare + _chestShare + _neckShare + _headShare;
        float invTotal = totalShare > 1.0e-4f ? 1.0f / totalShare : 0.0f;
        float spineWeight = _spineShare * invTotal;
        float chestWeight = _chestShare * invTotal * 0.55f;
        float upperChestWeight = _chestShare * invTotal * 0.45f;
        float neckWeight = _neckShare * invTotal;
        float headWeight = _headShare * invTotal;

        _slotBinding.WriteChannel(ValueStore, SpinePitchIndex, Math.Clamp(normPitch * spineWeight, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, SpineYawIndex, Math.Clamp(normYaw * spineWeight, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, ChestPitchIndex, Math.Clamp(normPitch * chestWeight, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, ChestYawIndex, Math.Clamp(normYaw * chestWeight, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, UpperChestPitchIndex, Math.Clamp(normPitch * upperChestWeight, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, UpperChestYawIndex, Math.Clamp(normYaw * upperChestWeight, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, NeckPitchIndex, Math.Clamp(normPitch * neckWeight, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, NeckYawIndex, Math.Clamp(normYaw * neckWeight, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, HeadPitchIndex, Math.Clamp(normPitch * headWeight, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, HeadYawIndex, Math.Clamp(normYaw * headWeight, -1.0f, 1.0f));

        // Optional arm aim offset gated by ArmAimWeight.
        float effectiveArm = aimWeight * armAimWeight * _armAimScale;
        float armPitch = Math.Clamp(normPitch * effectiveArm, -1.0f, 1.0f);
        float armYaw = Math.Clamp(normYaw * effectiveArm, -1.0f, 1.0f);
        _slotBinding.WriteChannel(ValueStore, LeftShoulderPitchIndex, armPitch * 0.35f);
        _slotBinding.WriteChannel(ValueStore, LeftArmPitchIndex, armPitch);
        _slotBinding.WriteChannel(ValueStore, LeftArmYawIndex, armYaw);
        _slotBinding.WriteChannel(ValueStore, RightShoulderPitchIndex, armPitch * 0.35f);
        _slotBinding.WriteChannel(ValueStore, RightArmPitchIndex, armPitch);
        _slotBinding.WriteChannel(ValueStore, RightArmYawIndex, -armYaw);
    }

    internal static void StepCriticallyDampedSpring(
        ref float current,
        ref float velocity,
        float target,
        float frequencyHz,
        float dt)
    {
        float omega = 2.0f * MathF.PI * MathF.Max(0.1f, frequencyHz);
        float f = 1.0f + 2.0f * dt * omega;
        float oo = omega * omega;
        float hoo = dt * oo;
        float hhoo = dt * hoo;
        float detInv = 1.0f / (f + hhoo);
        float detX = f * current + dt * velocity + hhoo * target;
        float detV = velocity + hoo * (target - current);
        current = detX * detInv;
        velocity = detV * detInv;
    }
}

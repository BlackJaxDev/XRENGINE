using System;
using System.Collections.Generic;
using System.Numerics;
using MemoryPack;
using XREngine.Components.Animation;

namespace XREngine.Animation;

/// <summary>
/// Additive procedural <see cref="MotionBase"/> that drives low-frequency breathing scaled
/// by exertion, acceleration-based forward/backward lean, and turn-rate lateral spine banking
/// with critically damped spring smoothing and zero per-frame allocations.
/// </summary>
[MemoryPackable]
public partial class ProceduralSecondaryMotion : MotionBase
{
    private const int SpineFrontBackIndex = 0;
    private const int SpineLeftRightIndex = 1;
    private const int ChestFrontBackIndex = 2;
    private const int ChestLeftRightIndex = 3;
    private const int UpperChestFrontBackIndex = 4;
    private const int LeftShoulderDownUpIndex = 5;
    private const int RightShoulderDownUpIndex = 6;

    public static readonly EHumanoidValue[] DrivenMuscleChannels =
    [
        EHumanoidValue.SpineFrontBack,
        EHumanoidValue.SpineLeftRight,
        EHumanoidValue.ChestFrontBack,
        EHumanoidValue.ChestLeftRight,
        EHumanoidValue.UpperChestFrontBack,
        EHumanoidValue.LeftShoulderDownUp,
        EHumanoidValue.RightShoulderDownUp,
    ];

    private string _moveXParameter = "MoveX";
    private string _moveYParameter = "MoveY";
    private string _desiredFacingYawParameter = "DesiredFacingYaw";
    private string _exertionParameter = "Exertion";
    private bool _enableBreathing = true;
    private float _breathingBaseFrequencyHz = 0.24f;
    private float _breathingMaxFrequencyHz = 0.65f;
    private float _breathingAmplitude = 0.06f;
    private float _leanGain = 0.14f;
    private float _maxLeanAmplitude = 0.22f;
    private float _bankGain = 0.08f;
    private float _maxBankAmplitude = 0.20f;
    private float _springFrequencyHz = 5.5f;

    [MemoryPackIgnore]
    private readonly HumanoidMuscleSlotBinding _slotBinding = new(DrivenMuscleChannels);

    [MemoryPackIgnore]
    private float _breathingPhaseRadians;

    [MemoryPackIgnore]
    private Vector2 _previousMoveInput;

    [MemoryPackIgnore]
    private float _previousYawRadians;

    [MemoryPackIgnore]
    private bool _hasHistory;

    [MemoryPackIgnore]
    private float _smoothedLean;

    [MemoryPackIgnore]
    private float _leanVelocity;

    [MemoryPackIgnore]
    private float _smoothedBank;

    [MemoryPackIgnore]
    private float _bankVelocity;

    [MemoryPackIgnore]
    private float _pendingDeltaSeconds;

    [MemoryPackIgnore]
    private bool _hasPendingTick;

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

    public string ExertionParameter
    {
        get => _exertionParameter;
        set => SetField(ref _exertionParameter, value ?? string.Empty);
    }

    public bool EnableBreathing
    {
        get => _enableBreathing;
        set => SetField(ref _enableBreathing, value);
    }

    public float BreathingBaseFrequencyHz
    {
        get => _breathingBaseFrequencyHz;
        set => SetField(ref _breathingBaseFrequencyHz, float.IsFinite(value) ? Math.Clamp(value, 0.05f, 3.0f) : 0.24f);
    }

    public float BreathingMaxFrequencyHz
    {
        get => _breathingMaxFrequencyHz;
        set => SetField(ref _breathingMaxFrequencyHz, float.IsFinite(value) ? Math.Clamp(value, _breathingBaseFrequencyHz, 5.0f) : 0.65f);
    }

    public float BreathingAmplitude
    {
        get => _breathingAmplitude;
        set => SetField(ref _breathingAmplitude, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 0.5f) : 0.06f);
    }

    public float LeanGain
    {
        get => _leanGain;
        set => SetField(ref _leanGain, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.14f);
    }

    public float MaxLeanAmplitude
    {
        get => _maxLeanAmplitude;
        set => SetField(ref _maxLeanAmplitude, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 0.8f) : 0.22f);
    }

    public float BankGain
    {
        get => _bankGain;
        set => SetField(ref _bankGain, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.08f);
    }

    public float MaxBankAmplitude
    {
        get => _maxBankAmplitude;
        set => SetField(ref _maxBankAmplitude, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 0.8f) : 0.20f);
    }

    public float SpringFrequencyHz
    {
        get => _springFrequencyHz;
        set => SetField(ref _springFrequencyHz, float.IsFinite(value) ? Math.Clamp(value, 0.5f, 30.0f) : 5.5f);
    }

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
        _breathingPhaseRadians = 0.0f;
        _previousMoveInput = Vector2.Zero;
        _previousYawRadians = 0.0f;
        _hasHistory = false;
        _smoothedLean = 0.0f;
        _leanVelocity = 0.0f;
        _smoothedBank = 0.0f;
        _bankVelocity = 0.0f;
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
        EvaluateProceduralMotionValues(variables, 0.0, additive: true);
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

        var moveInput = new Vector2(
            ReadVariableFloat(variables, _moveXParameter, 0.0f),
            ReadVariableFloat(variables, _moveYParameter, 0.0f));
        float moveSpeed = Math.Clamp(moveInput.Length(), 0.0f, 1.5f);
        float facingYaw = ReadVariableFloat(variables, _desiredFacingYawParameter, 0.0f);
        float exertion = Math.Clamp(ReadVariableFloat(variables, _exertionParameter, moveSpeed * 0.65f), 0.0f, 1.0f);

        float targetLean = 0.0f;
        float targetBank = 0.0f;
        if (_hasHistory && dt > 1.0e-4f)
        {
            float prevSpeed = _previousMoveInput.Length();
            float accel = (moveSpeed - prevSpeed) / dt;
            float steadyPitch = moveSpeed * 0.45f;
            targetLean = Math.Clamp((accel * 0.25f + steadyPitch) * _leanGain, -_maxLeanAmplitude, _maxLeanAmplitude);

            float yawDelta = WrapAngleRadians(facingYaw - _previousYawRadians);
            float yawRate = yawDelta / dt;
            targetBank = Math.Clamp(yawRate * moveSpeed * _bankGain, -_maxBankAmplitude, _maxBankAmplitude);
        }

        _previousMoveInput = moveInput;
        _previousYawRadians = facingYaw;
        _hasHistory = true;

        if (dt > 1.0e-5f)
        {
            HumanoidLookAtAimMotion.StepCriticallyDampedSpring(ref _smoothedLean, ref _leanVelocity, targetLean, _springFrequencyHz, dt);
            HumanoidLookAtAimMotion.StepCriticallyDampedSpring(ref _smoothedBank, ref _bankVelocity, targetBank, _springFrequencyHz, dt);
        }
        else
        {
            _smoothedLean = targetLean;
            _smoothedBank = targetBank;
        }

        _smoothedLean = Math.Clamp(_smoothedLean, -_maxLeanAmplitude, _maxLeanAmplitude);
        _smoothedBank = Math.Clamp(_smoothedBank, -_maxBankAmplitude, _maxBankAmplitude);

        float breathWave = 0.0f;
        if (_enableBreathing)
        {
            float freq = _breathingBaseFrequencyHz + (_breathingMaxFrequencyHz - _breathingBaseFrequencyHz) * exertion;
            _breathingPhaseRadians = (_breathingPhaseRadians + dt * freq * MathF.Tau) % MathF.Tau;
            float amp = _breathingAmplitude * (1.0f + exertion * 0.85f);
            breathWave = MathF.Sin(_breathingPhaseRadians) * amp;
        }

        _slotBinding.WriteChannel(ValueStore, SpineFrontBackIndex, Math.Clamp(-_smoothedLean * 0.55f + breathWave * 0.35f, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, SpineLeftRightIndex, Math.Clamp(_smoothedBank * 0.55f, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, ChestFrontBackIndex, Math.Clamp(-_smoothedLean * 0.30f + breathWave * 0.65f, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, ChestLeftRightIndex, Math.Clamp(_smoothedBank * 0.45f, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, UpperChestFrontBackIndex, Math.Clamp(-_smoothedLean * 0.15f + breathWave, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, LeftShoulderDownUpIndex, Math.Clamp(breathWave * 0.45f, -1.0f, 1.0f));
        _slotBinding.WriteChannel(ValueStore, RightShoulderDownUpIndex, Math.Clamp(breathWave * 0.45f, -1.0f, 1.0f));
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

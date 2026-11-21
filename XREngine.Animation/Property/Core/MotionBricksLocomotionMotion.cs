using System.Numerics;
using MemoryPack;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;

namespace XREngine.Animation;

/// <summary>
/// First-class <see cref="MotionBase"/> asset that drives humanoid body/limb muscles and
/// root motion from an NVIDIA MotionBricks SOMA locomotion runtime inside
/// <see cref="AnimStateMachine"/>.
/// </summary>
[MemoryPackable]
public partial class MotionBricksLocomotionMotion : MotionBase, IHumanoidProceduralRootMotionSource
{
    /// <summary>
    /// Canonical body and limb <see cref="EHumanoidValue"/> channels written by SOMA
    /// locomotion. Fingers, jaw, and eyes are excluded so upper-body, gesture, and facial
    /// layers retain uncontested ownership of those slots.
    /// </summary>
    public static readonly EHumanoidValue[] DrivenMuscleChannels =
    [
        EHumanoidValue.SpineFrontBack,
        EHumanoidValue.SpineLeftRight,
        EHumanoidValue.SpineTwistLeftRight,
        EHumanoidValue.ChestFrontBack,
        EHumanoidValue.ChestLeftRight,
        EHumanoidValue.ChestTwistLeftRight,
        EHumanoidValue.UpperChestFrontBack,
        EHumanoidValue.UpperChestLeftRight,
        EHumanoidValue.UpperChestTwistLeftRight,
        EHumanoidValue.NeckNodDownUp,
        EHumanoidValue.NeckTiltLeftRight,
        EHumanoidValue.NeckTurnLeftRight,
        EHumanoidValue.HeadNodDownUp,
        EHumanoidValue.HeadTiltLeftRight,
        EHumanoidValue.HeadTurnLeftRight,
        EHumanoidValue.LeftShoulderDownUp,
        EHumanoidValue.LeftShoulderFrontBack,
        EHumanoidValue.LeftArmDownUp,
        EHumanoidValue.LeftArmFrontBack,
        EHumanoidValue.LeftArmTwistInOut,
        EHumanoidValue.LeftForearmStretch,
        EHumanoidValue.LeftForearmTwistInOut,
        EHumanoidValue.LeftHandDownUp,
        EHumanoidValue.LeftHandInOut,
        EHumanoidValue.RightShoulderDownUp,
        EHumanoidValue.RightShoulderFrontBack,
        EHumanoidValue.RightArmDownUp,
        EHumanoidValue.RightArmFrontBack,
        EHumanoidValue.RightArmTwistInOut,
        EHumanoidValue.RightForearmStretch,
        EHumanoidValue.RightForearmTwistInOut,
        EHumanoidValue.RightHandDownUp,
        EHumanoidValue.RightHandInOut,
        EHumanoidValue.LeftUpperLegFrontBack,
        EHumanoidValue.LeftUpperLegInOut,
        EHumanoidValue.LeftUpperLegTwistInOut,
        EHumanoidValue.LeftLowerLegStretch,
        EHumanoidValue.LeftLowerLegTwistInOut,
        EHumanoidValue.LeftFootUpDown,
        EHumanoidValue.LeftFootTwistInOut,
        EHumanoidValue.LeftToesUpDown,
        EHumanoidValue.RightUpperLegFrontBack,
        EHumanoidValue.RightUpperLegInOut,
        EHumanoidValue.RightUpperLegTwistInOut,
        EHumanoidValue.RightLowerLegStretch,
        EHumanoidValue.RightLowerLegTwistInOut,
        EHumanoidValue.RightFootUpDown,
        EHumanoidValue.RightFootTwistInOut,
        EHumanoidValue.RightToesUpDown,
    ];

    public static readonly ImportedHumanoidRootMotionPolicy DefaultRootMotionPolicy = new(
        StartTime: 0.0f,
        StopTime: 1.0f,
        OrientationOffsetY: 0.0f,
        Level: 0.0f,
        CycleOffset: 0.0f,
        LoopTime: false,
        LoopPose: false,
        BakeOrientationIntoPose: false,
        BakePositionYIntoPose: true,
        BakePositionXZIntoPose: false,
        OrientationBasis: EImportedHumanoidRootOrientationBasis.Body,
        PositionYBasis: EImportedHumanoidRootPositionYBasis.Original,
        PositionXZBasis: EImportedHumanoidRootPositionXZBasis.CenterOfMass,
        Mirror: false);

    private string _modelProfileId = "soma-rp-v1";
    private string _locomotionPresetId = "locomotion_bs2_directional";
    private string _moveXParameter = "MoveX";
    private string _moveYParameter = "MoveY";
    private string _desiredFacingYawParameter = "DesiredFacingYaw";
    private string _strafeModeParameter = "StrafeMode";
    private string _styleParameter = "LocomotionStyle";
    private string _isGroundedParameter = "IsGrounded";
    private float _walkSpeedMetersPerSecond = 1.5f;
    private float _runSpeedMetersPerSecond = 4.2f;
    private float _replanIntervalSeconds = 4.0f / 30.0f;
    private float _inputReplanThreshold = 0.15f;
    private bool _bakeOrientationIntoPose;
    private bool _bakePositionYIntoPose = true;
    private bool _bakePositionXZIntoPose;

    [MemoryPackIgnore]
    private readonly MotionBricksEvaluatedPose _evaluatedPose = new();

    [MemoryPackIgnore]
    private AnimationMember? _bindingRootMember;

    [MemoryPackIgnore]
    private AnimationMember[] _muscleBindingMembers = [];

    [MemoryPackIgnore]
    private int[] _muscleSlotIndices = [];

    [MemoryPackIgnore]
    private IMotionBricksLocomotionDriver? _runtimeDriver;

    [MemoryPackIgnore]
    private bool _ownsRuntimeDriver;

    [MemoryPackIgnore]
    private float _pendingDeltaSeconds;

    [MemoryPackIgnore]
    private bool _hasPendingTick;

    public string ModelProfileId
    {
        get => _modelProfileId;
        set => SetField(ref _modelProfileId, string.IsNullOrWhiteSpace(value) ? "soma-rp-v1" : value);
    }

    public string LocomotionPresetId
    {
        get => _locomotionPresetId;
        set => SetField(ref _locomotionPresetId, string.IsNullOrWhiteSpace(value) ? "locomotion_bs2_directional" : value);
    }

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
        set => SetField(ref _walkSpeedMetersPerSecond, float.IsFinite(value) ? MathF.Max(0.1f, value) : 1.5f);
    }

    public float RunSpeedMetersPerSecond
    {
        get => _runSpeedMetersPerSecond;
        set => SetField(ref _runSpeedMetersPerSecond, float.IsFinite(value) ? MathF.Max(0.1f, value) : 4.2f);
    }

    public float ReplanIntervalSeconds
    {
        get => _replanIntervalSeconds;
        set => SetField(ref _replanIntervalSeconds, float.IsFinite(value) ? MathF.Max(1.0f / 30.0f, value) : 4.0f / 30.0f);
    }

    public float InputReplanThreshold
    {
        get => _inputReplanThreshold;
        set => SetField(ref _inputReplanThreshold, float.IsFinite(value) ? Math.Clamp(value, 0.01f, 2.0f) : 0.15f);
    }

    public bool BakeOrientationIntoPose
    {
        get => _bakeOrientationIntoPose;
        set => SetField(ref _bakeOrientationIntoPose, value);
    }

    public bool BakePositionYIntoPose
    {
        get => _bakePositionYIntoPose;
        set => SetField(ref _bakePositionYIntoPose, value);
    }

    public bool BakePositionXZIntoPose
    {
        get => _bakePositionXZIntoPose;
        set => SetField(ref _bakePositionXZIntoPose, value);
    }

    /// <summary>
    /// Optional explicit runtime driver override (used by tests or custom scene setups).
    /// When null, initialization resolves a driver from <see cref="MotionBricksLocomotionRuntimeServices.CurrentFactory"/>.
    /// </summary>
    [MemoryPackIgnore]
    public IMotionBricksLocomotionDriver? RuntimeDriverOverride { get; set; }

    [MemoryPackIgnore]
    public IMotionBricksLocomotionDriver? ActiveDriver => _runtimeDriver;

    [MemoryPackIgnore]
    public MotionBricksEvaluatedPose EvaluatedPose => _evaluatedPose;

    [MemoryPackIgnore]
    public string RuntimeDiagnosticStatus => _runtimeDriver?.DiagnosticStatus
        ?? "No MotionBricks locomotion runtime factory is registered.";

    public ImportedHumanoidRootMotionPolicy BuildRootMotionPolicy()
        => DefaultRootMotionPolicy with
        {
            BakeOrientationIntoPose = _bakeOrientationIntoPose,
            BakePositionYIntoPose = _bakePositionYIntoPose,
            BakePositionXZIntoPose = _bakePositionXZIntoPose,
        };

    /// <summary>
    /// Ensures the canonical humanoid muscle binding tree exists and returns its root member
    /// for <see cref="MotionBase.InitializeMotion"/> registration.
    /// </summary>
    internal AnimationMember EnsureBindingRootMember()
    {
        if (_bindingRootMember is not null)
            return _bindingRootMember;

        var root = new AnimationMember(string.Empty, EAnimationMemberType.Group);
        var sceneNode = new AnimationMember("SceneNode", EAnimationMemberType.Property);
        root.Children.Add(sceneNode);

        // Matches SerializedAnimationImporter.AddHumanoidValueAnimation so MotionBricks
        // shares exact AnimSlot indices with imported Unity humanoid clips.
        var getHumanoid = new AnimationMember("GetComponentInHierarchy", EAnimationMemberType.Method)
        {
            MethodArguments = ["HumanoidComponent"],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        sceneNode.Children.Add(getHumanoid);

        var members = new AnimationMember[DrivenMuscleChannels.Length];
        for (int i = 0; i < DrivenMuscleChannels.Length; i++)
        {
            EHumanoidValue muscle = DrivenMuscleChannels[i];
            var setter = new AnimationMember("SetImportedRawValue", EAnimationMemberType.Method)
            {
                MethodArguments = [muscle, 0.0f, false],
                AnimatedMethodArgumentIndex = 1,
                CacheReturnValue = false,
                DefaultValue = 0.0f,
            };
            getHumanoid.Children.Add(setter);
            members[i] = setter;
        }

        _bindingRootMember = root;
        _muscleBindingMembers = members;
        _muscleSlotIndices = new int[DrivenMuscleChannels.Length];
        Array.Fill(_muscleSlotIndices, -1);
        return root;
    }

    internal void BindRuntimeDriver()
    {
        if (RuntimeDriverOverride is not null)
        {
            if (!ReferenceEquals(_runtimeDriver, RuntimeDriverOverride))
            {
                ReleaseOwnedDriver();
                _runtimeDriver = RuntimeDriverOverride;
                _ownsRuntimeDriver = false;
            }
            return;
        }

        if (_runtimeDriver is not null)
            return;

        IMotionBricksLocomotionDriverFactory? factory = MotionBricksLocomotionRuntimeServices.CurrentFactory;
        if (factory is not null)
        {
            _runtimeDriver = factory.CreateDriver(this);
            _ownsRuntimeDriver = true;
        }
    }

    internal void CacheResolvedMuscleSlots()
    {
        if (_muscleSlotIndices.Length != _muscleBindingMembers.Length)
            _muscleSlotIndices = new int[_muscleBindingMembers.Length];

        for (int i = 0; i < _muscleBindingMembers.Length; i++)
        {
            AnimSlot slot = _muscleBindingMembers[i].Slot;
            _muscleSlotIndices[i] = slot.IsValid && slot.Type == EAnimValueType.Float
                ? slot.TypeIndex
                : -1;
        }
    }

    internal void ReleaseRuntimeDriver()
    {
        ReleaseOwnedDriver();
        _runtimeDriver = null;
        _evaluatedPose.Clear();
        _pendingDeltaSeconds = 0.0f;
        _hasPendingTick = false;
    }

    internal void NotifyStateEntered()
    {
        BindRuntimeDriver();
        _runtimeDriver?.OnStateEntered();
    }

    internal void NotifyStateExited()
        => _runtimeDriver?.OnStateExited();

    internal void AccumulateTickDelta(float deltaSeconds)
    {
        if (float.IsFinite(deltaSeconds) && deltaSeconds > 0.0f)
            _pendingDeltaSeconds += deltaSeconds;
        _hasPendingTick = true;
    }

    internal void EvaluateMotionBricksValues(IDictionary<string, AnimVar> variables, bool additive)
    {
        BindRuntimeDriver();
        if (_muscleSlotIndices.Length != DrivenMuscleChannels.Length
            || (_muscleSlotIndices.Length > 0 && _muscleSlotIndices[0] < 0))
        {
            CacheResolvedMuscleSlots();
        }

        float deltaSeconds = _hasPendingTick ? _pendingDeltaSeconds : 0.0f;
        _pendingDeltaSeconds = 0.0f;
        _hasPendingTick = false;

        if (_runtimeDriver is not null)
        {
            MotionBricksSteeringInput steering = ReadSteeringInput(variables, deltaSeconds);
            _runtimeDriver.AdvanceAndEvaluate(steering, _evaluatedPose);
        }

        float[] muscles = _evaluatedPose.MuscleValues;
        bool hasPose = _evaluatedPose.HasValidPose;
        for (int i = 0; i < DrivenMuscleChannels.Length; i++)
        {
            int slotIndex = _muscleSlotIndices[i];
            if (slotIndex < 0)
                continue;

            float value = hasPose ? muscles[(int)DrivenMuscleChannels[i]] : 0.0f;
            if (!float.IsFinite(value))
                value = 0.0f;

            // For additive layers, neutral zero produces no offset.
            ValueStore.SetFloat(slotIndex, additive ? value : value);
        }
    }

    internal bool TryCreateProceduralHumanoidContribution(
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        out HumanoidMotionContribution contribution)
    {
        if (!_evaluatedPose.HasValidPose || !float.IsFinite(weight) || weight <= 0.0f)
        {
            contribution = default;
            return false;
        }

        contribution = new HumanoidMotionContribution(
            Clip: null,
            Policy: BuildRootMotionPolicy(),
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

    public bool TryGetBodySamples(
        out Vector3 canonicalBodyPosition,
        out Quaternion canonicalBodyRotation,
        out Vector3 currentBodyPosition,
        out Quaternion currentBodyRotation)
    {
        if (!_evaluatedPose.HasValidPose)
        {
            canonicalBodyPosition = Vector3.Zero;
            canonicalBodyRotation = Quaternion.Identity;
            currentBodyPosition = Vector3.Zero;
            currentBodyRotation = Quaternion.Identity;
            return false;
        }

        canonicalBodyPosition = _evaluatedPose.CanonicalBodyPosition;
        canonicalBodyRotation = _evaluatedPose.CanonicalBodyRotation;
        currentBodyPosition = _evaluatedPose.CurrentBodyPosition;
        currentBodyRotation = _evaluatedPose.CurrentBodyRotation;
        return true;
    }

    public void CopyProjectionMuscles(Span<float> destination)
    {
        float[] source = _evaluatedPose.MuscleValues;
        int count = Math.Min(source.Length, destination.Length);
        source.AsSpan(0, count).CopyTo(destination);
    }

    public override void Tick(float delta)
        => AccumulateTickDelta(delta);

    public override void GetAnimationValues(MotionBase? parentMotion, IDictionary<string, AnimVar> variables, float weight)
    {
        EvaluateMotionBricksValues(variables, additive: false);
        if (parentMotion is not null && SlotLayout is not null && !ReferenceEquals(parentMotion, this))
            parentMotion.ValueStore.CopyFrom(ValueStore);
    }

    internal void RestartMotionBricksPlayback()
    {
        _pendingDeltaSeconds = 0.0f;
        _hasPendingTick = false;
        _runtimeDriver?.ResetPlayback();
    }

    internal void StopMotionBricksPlayback()
    {
        _pendingDeltaSeconds = 0.0f;
        _hasPendingTick = false;
        _runtimeDriver?.OnStateExited();
    }

    private MotionBricksSteeringInput ReadSteeringInput(IDictionary<string, AnimVar> variables, float deltaSeconds)
    {
        float moveX = ReadFloat(variables, _moveXParameter, 0.0f);
        float moveY = ReadFloat(variables, _moveYParameter, 0.0f);
        float facingYaw = ReadFloat(variables, _desiredFacingYawParameter, 0.0f);
        bool strafeMode = ReadBool(variables, _strafeModeParameter, false);
        int styleIndex = ReadInt(variables, _styleParameter, 0);
        bool isGrounded = ReadBool(variables, _isGroundedParameter, true);

        return new MotionBricksSteeringInput(
            new Vector2(moveX, moveY),
            facingYaw,
            strafeMode,
            styleIndex,
            isGrounded,
            _walkSpeedMetersPerSecond,
            _runSpeedMetersPerSecond,
            _replanIntervalSeconds,
            _inputReplanThreshold,
            deltaSeconds);
    }

    private static float ReadFloat(IDictionary<string, AnimVar> variables, string name, float fallback)
    {
        if (!string.IsNullOrEmpty(name) && variables.TryGetValue(name, out AnimVar? animVar) && float.IsFinite(animVar.FloatValue))
            return animVar.FloatValue;

        return fallback;
    }

    private static int ReadInt(IDictionary<string, AnimVar> variables, string name, int fallback)
    {
        if (!string.IsNullOrEmpty(name) && variables.TryGetValue(name, out AnimVar? animVar))
            return animVar.IntValue;

        return fallback;
    }

    private static bool ReadBool(IDictionary<string, AnimVar> variables, string name, bool fallback)
    {
        if (!string.IsNullOrEmpty(name) && variables.TryGetValue(name, out AnimVar? animVar))
            return animVar.BoolValue;

        return fallback;
    }

    private void ReleaseOwnedDriver()
    {
        if (_ownsRuntimeDriver && _runtimeDriver is not null)
            _runtimeDriver.Dispose();

        _ownsRuntimeDriver = false;
    }
}

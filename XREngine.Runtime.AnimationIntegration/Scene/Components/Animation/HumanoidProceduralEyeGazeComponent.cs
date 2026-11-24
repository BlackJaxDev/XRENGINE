using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Attributes;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Drives natural eye saccades, locomotion-matched turn look-ahead, idle ambient look-around,
/// and realistic asymmetric blinking on a <see cref="HumanoidComponent"/> avatar using native
/// humanoid eye muscles and auto-discovered face blink blendshapes without per-frame allocations.
/// </summary>
[Category("Animation")]
[DisplayName("Humanoid Procedural Eye Gaze")]
[Description("Drives locomotion-matched eye gaze, idle look-around saccades, and natural blinking on a humanoid avatar.")]
public sealed class HumanoidProceduralEyeGazeComponent : XRComponent
{
    private readonly struct BlinkBinding
    {
        public readonly ModelComponent Model;
        public readonly string BlendshapeName;

        public BlinkBinding(ModelComponent model, string blendshapeName)
        {
            Model = model;
            BlendshapeName = blendshapeName;
        }
    }

    private static readonly (string Left, string Right)[] BilateralBlinkCandidates =
    [
        ("eyeBlinkLeft", "eyeBlinkRight"),
        ("EyeBlinkLeft", "EyeBlinkRight"),
        ("vrc.blink_left", "vrc.blink_right"),
        ("Blink_L", "Blink_R"),
        ("blink_l", "blink_r"),
        ("Fcl_EYE_Close_L", "Fcl_EYE_Close_R"),
        ("EyeClose_L", "EyeClose_R"),
        ("eye_close_l", "eye_close_r"),
    ];

    private static readonly string[] CombinedBlinkCandidates =
    [
        "vrc.blink",
        "Blink",
        "blink",
        "Fcl_EYE_Close",
        "EyeClose",
        "Eye_Close",
        "EyesClosed",
        "まばたき",
        "瞳閉じ",
    ];

    private const float SaccadeResponseRatePerSecond = 38.0f;
    private const float BlinkCloseSeconds = 0.055f;
    private const float BlinkHoldSeconds = 0.025f;
    private const float BlinkOpenSeconds = 0.115f;
    private const float BlinkTotalSeconds = BlinkCloseSeconds + BlinkHoldSeconds + BlinkOpenSeconds;
    private const float BlinkRescanIntervalSeconds = 0.50f;

    private HumanoidComponent? _humanoid;
    private AnimStateMachineComponent? _animator;
    private TransformBase? _avatarFacingTransform;
    private TransformBase? _viewerCameraTransform;
    private string _moveXParameter = "MoveX";
    private string _moveYParameter = "MoveY";
    private string _styleParameter = "LocomotionStyle";
    private string _isGroundedParameter = "IsGrounded";
    private string _verticalVelocityParameter = "VerticalVelocity";
    private bool _enableBlinking = true;
    private bool _enableCameraGlances = true;

    private readonly List<BlinkBinding> _blinkBindings = [];
    private bool _blinkScanCompleted;
    private float _blinkRescanCooldownSeconds;

    private long _lastTickTimestamp;
    private uint _rngState = 0x5EED1234u;

    private float _currentYaw;
    private float _currentPitch = -0.14f;
    private float _targetYaw;
    private float _targetPitch = -0.14f;
    private float _currentVergence = 0.02f;
    private float _targetVergence = 0.02f;
    private float _fixationRemainingSeconds = 1.4f;
    private float _locomotionGlanceYaw;
    private float _locomotionGlancePitch;
    private float _locomotionGlanceRemainingSeconds = 2.2f;
    private bool _wasMoving;

    private float _blinkElapsedSeconds = -1.0f;
    private float _timeUntilNextBlinkSeconds = 2.6f;
    private float _timeSinceLastBlinkSeconds = 2.0f;
    private bool _doubleBlinkPending;
    private float _lastAppliedBlinkWeight = -1.0f;

    /// <summary>
    /// Target <see cref="HumanoidComponent"/> whose native eye muscles are driven before pose solve.
    /// Defaults to a sibling component on the same <see cref="SceneNode"/> when unset.
    /// </summary>
    public HumanoidComponent? Humanoid
    {
        get => _humanoid;
        set => SetField(ref _humanoid, value);
    }

    /// <summary>
    /// Optional <see cref="AnimStateMachineComponent"/> providing live locomotion steering parameters
    /// (<c>MoveX</c>, <c>MoveY</c>, <c>LocomotionStyle</c>, <c>IsGrounded</c>, <c>VerticalVelocity</c>).
    /// </summary>
    public AnimStateMachineComponent? Animator
    {
        get => _animator;
        set => SetField(ref _animator, value);
    }

    /// <summary>
    /// Optional avatar root transform used as a fallback forward reference when reading travel turns.
    /// </summary>
    public TransformBase? AvatarFacingTransform
    {
        get => _avatarFacingTransform;
        set => SetField(ref _avatarFacingTransform, value);
    }

    /// <summary>
    /// Optional camera transform that the avatar may briefly glance toward during calm idle when
    /// the camera is in front of her face.
    /// </summary>
    public TransformBase? ViewerCameraTransform
    {
        get => _viewerCameraTransform;
        set => SetField(ref _viewerCameraTransform, value);
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

    public string VerticalVelocityParameter
    {
        get => _verticalVelocityParameter;
        set => SetField(ref _verticalVelocityParameter, value ?? string.Empty);
    }

    /// <summary>
    /// Enables natural periodic blinking and subtle downward-gaze eyelid droop on discovered face blendshapes.
    /// </summary>
    public bool EnableBlinking
    {
        get => _enableBlinking;
        set => SetField(ref _enableBlinking, value);
    }

    /// <summary>
    /// Enables occasional natural idle glances toward <see cref="ViewerCameraTransform"/> when visible in front of the avatar.
    /// </summary>
    public bool EnableCameraGlances
    {
        get => _enableCameraGlances;
        set => SetField(ref _enableCameraGlances, value);
    }

    public float CurrentGazeYaw => _currentYaw;
    public float CurrentGazePitch => _currentPitch;
    public float CurrentBlinkWeight => MathF.Max(0.0f, _lastAppliedBlinkWeight);

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _lastTickTimestamp = Stopwatch.GetTimestamp();
        _blinkScanCompleted = false;
        _blinkRescanCooldownSeconds = 0.0f;
        _blinkBindings.Clear();
        _lastAppliedBlinkWeight = -1.0f;

        // Tick in ETickOrder.Animation so eye muscles are written right before HumanoidComponent
        // solves and commits bone transforms in ETickOrder.Scene.
        RegisterTick(ETickGroup.Normal, ETickOrder.Animation, TickProceduralEyes);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, TickProceduralEyes);
        ApplyBlinkWeightToBindings(0.0f);
        _blinkBindings.Clear();
        _blinkScanCompleted = false;
        base.OnComponentDeactivated();
    }

    /// <summary>
    /// Advances procedural eye gaze and blinking by <paramref name="deltaSeconds"/> and writes the
    /// resulting eye muscles and blink weights. Exposed for deterministic unit testing.
    /// </summary>
    public void Step(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0.0f)
            return;

        float dt = Math.Clamp(deltaSeconds, 0.0f, 0.10f);
        HumanoidComponent? humanoid = ResolveHumanoid();
        if (humanoid is null)
            return;

        ReadLocomotionSignals(
            out Vector2 moveWorldXZ,
            out int styleIndex,
            out bool isGrounded,
            out float verticalVelocity);

        float moveMag = moveWorldXZ.Length();
        bool isMoving = moveMag > 0.08f;

        ResolveHeadBasis(humanoid, out Vector3 headPos, out Vector3 headForward, out Vector3 headLeft, out Vector3 headUp);

        if (isMoving)
            UpdateLocomotionGaze(dt, moveWorldXZ, moveMag, styleIndex, isGrounded, verticalVelocity, headForward, headLeft);
        else
            UpdateIdleGaze(dt, styleIndex, isGrounded, verticalVelocity, headPos, headForward, headLeft, headUp);

        _wasMoving = isMoving;

        // Advance eyes with crisp ballistic saccade dynamics (human eyes snap quickly then hold still).
        float eyeAlpha = 1.0f - MathF.Exp(-SaccadeResponseRatePerSecond * dt);
        _currentYaw += (_targetYaw - _currentYaw) * eyeAlpha;
        _currentPitch += (_targetPitch - _currentPitch) * eyeAlpha;
        _currentVergence += (_targetVergence - _currentVergence) * eyeAlpha;

        float leftEyeInOut = Math.Clamp(_currentYaw - _currentVergence, -1.0f, 1.0f);
        float rightEyeInOut = Math.Clamp(-_currentYaw - _currentVergence, -1.0f, 1.0f);
        float eyePitch = Math.Clamp(_currentPitch, -1.0f, 1.0f);

        humanoid.SetValue(EHumanoidValue.LeftEyeDownUp, eyePitch);
        humanoid.SetValue(EHumanoidValue.RightEyeDownUp, eyePitch);
        humanoid.SetValue(EHumanoidValue.LeftEyeInOut, leftEyeInOut);
        humanoid.SetValue(EHumanoidValue.RightEyeInOut, rightEyeInOut);

        if (_enableBlinking)
            UpdateBlinking(dt);
        else if (_lastAppliedBlinkWeight > 0.0f)
            ApplyBlinkWeightToBindings(0.0f);
    }

    private void TickProceduralEyes()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _lastTickTimestamp > 0L
            ? (float)((now - _lastTickTimestamp) / (double)Stopwatch.Frequency)
            : 1.0f / 60.0f;
        _lastTickTimestamp = now;
        Step(dt);
    }

    private void UpdateLocomotionGaze(
        float dt,
        Vector2 moveWorldXZ,
        float moveMag,
        int styleIndex,
        bool isGrounded,
        float verticalVelocity,
        Vector3 headForward,
        Vector3 headLeft)
    {
        Vector2 forwardXZ = new(headForward.X, headForward.Z);
        float fwdLenSq = forwardXZ.LengthSquared();
        float turnAngleRadians = 0.0f;
        if (fwdLenSq > 1.0e-6f && moveMag > 1.0e-4f)
        {
            forwardXZ /= MathF.Sqrt(fwdLenSq);
            Vector2 leftXZ = new(headLeft.X, headLeft.Z);
            float leftLenSq = leftXZ.LengthSquared();
            leftXZ = leftLenSq > 1.0e-6f
                ? leftXZ / MathF.Sqrt(leftLenSq)
                : new Vector2(forwardXZ.Y, -forwardXZ.X);

            Vector2 moveDir = moveWorldXZ / moveMag;
            float fwdDot = Math.Clamp(Vector2.Dot(moveDir, forwardXZ), -1.0f, 1.0f);
            float leftDot = Math.Clamp(Vector2.Dot(moveDir, leftXZ), -1.0f, 1.0f);
            turnAngleRadians = MathF.Atan2(leftDot, fwdDot);
        }

        // Refresh brief natural environmental micro-glances while walking straight so gaze feels alive.
        _locomotionGlanceRemainingSeconds -= dt;
        if (!_wasMoving || _locomotionGlanceRemainingSeconds <= 0.0f)
        {
            _locomotionGlanceRemainingSeconds = NextRange(1.6f, 3.6f);
            if (MathF.Abs(turnAngleRadians) < 0.25f && NextFloat01() < 0.55f)
            {
                _locomotionGlanceYaw = NextRange(-0.28f, 0.28f);
                _locomotionGlancePitch = NextRange(-0.10f, 0.12f);
            }
            else
            {
                _locomotionGlanceYaw = 0.0f;
                _locomotionGlancePitch = 0.0f;
            }
        }

        // Eyes lead horizontal turns briskly while keeping downward pitch aligned with the travel path.
        float turnLeadYaw = Math.Clamp(turnAngleRadians * 1.15f, -0.78f, 0.78f);
        float glanceWeight = Math.Clamp(1.0f - MathF.Abs(turnAngleRadians) * 2.2f, 0.0f, 1.0f);
        float nextTargetYaw = Math.Clamp(turnLeadYaw + _locomotionGlanceYaw * glanceWeight, -0.82f, 0.82f);

        float basePitch;
        if (!isGrounded)
            basePitch = verticalVelocity > 0.5f ? 0.14f : -0.32f;
        else if (styleIndex == 1)
            basePitch = 0.06f;
        else if (styleIndex == 2)
            basePitch = 0.16f;
        else
            basePitch = moveMag > 0.75f ? -0.16f : -0.24f;

        float nextTargetPitch = Math.Clamp(basePitch + _locomotionGlancePitch * glanceWeight, -0.65f, 0.45f);
        MaybeTriggerSaccadeCoupledBlink(nextTargetYaw, nextTargetPitch);

        _targetYaw = nextTargetYaw;
        _targetPitch = nextTargetPitch;
        _targetVergence = 0.015f;
    }

    private void UpdateIdleGaze(
        float dt,
        int styleIndex,
        bool isGrounded,
        float verticalVelocity,
        Vector3 headPos,
        Vector3 headForward,
        Vector3 headLeft,
        Vector3 headUp)
    {
        if (!isGrounded)
        {
            _targetYaw = 0.0f;
            _targetPitch = verticalVelocity > 0.5f ? 0.16f : -0.34f;
            _targetVergence = 0.015f;
            return;
        }

        _fixationRemainingSeconds -= dt;
        if (_wasMoving)
        {
            // Settle calmly forward when stopping, then begin natural ambient look-around.
            _targetYaw = NextRange(-0.12f, 0.12f);
            _targetPitch = styleIndex == 1 ? 0.04f : -0.14f;
            _targetVergence = 0.02f;
            _fixationRemainingSeconds = NextRange(1.0f, 2.0f);
            return;
        }

        if (_fixationRemainingSeconds > 0.0f)
            return;

        float roll = NextFloat01();
        float nextYaw;
        float nextPitch;
        float nextVergence = 0.02f;

        if (_enableCameraGlances
            && roll < 0.24f
            && TryComputeCameraGlance(headPos, headForward, headLeft, headUp, out float camYaw, out float camPitch, out float camDist))
        {
            nextYaw = camYaw;
            nextPitch = camPitch;
            nextVergence = camDist < 2.2f ? 0.055f : 0.025f;
            _fixationRemainingSeconds = NextRange(1.4f, 2.8f);
        }
        else if (roll < 0.62f)
        {
            // Calm near-forward resting fixation with gentle natural variation.
            nextYaw = NextRange(-0.18f, 0.18f);
            nextPitch = styleIndex == 1 ? NextRange(-0.08f, 0.12f) : NextRange(-0.22f, 0.02f);
            _fixationRemainingSeconds = NextRange(1.5f, 3.2f);
        }
        else
        {
            // Curious ambient glance to the left, right, or horizon.
            float sideSign = NextFloat01() < 0.5f ? -1.0f : 1.0f;
            nextYaw = sideSign * NextRange(0.28f, 0.68f);
            nextPitch = NextRange(-0.24f, 0.16f);
            _fixationRemainingSeconds = NextRange(1.0f, 2.4f);
        }

        MaybeTriggerSaccadeCoupledBlink(nextYaw, nextPitch);
        _targetYaw = nextYaw;
        _targetPitch = nextPitch;
        _targetVergence = nextVergence;
    }

    private void MaybeTriggerSaccadeCoupledBlink(float nextTargetYaw, float nextTargetPitch)
    {
        if (!_enableBlinking || _blinkElapsedSeconds >= 0.0f || _timeSinceLastBlinkSeconds < 0.85f)
            return;

        float shiftMagnitude = MathF.Abs(nextTargetYaw - _currentYaw) + MathF.Abs(nextTargetPitch - _currentPitch);
        if (shiftMagnitude > 0.52f && NextFloat01() < 0.55f)
            StartBlink();
    }

    private void UpdateBlinking(float dt)
    {
        EnsureBlinkBindingsDiscovered(dt);
        if (_blinkBindings.Count == 0)
            return;

        _timeSinceLastBlinkSeconds += dt;

        float blinkEnvelope = 0.0f;
        if (_blinkElapsedSeconds >= 0.0f)
        {
            _blinkElapsedSeconds += dt;
            if (_blinkElapsedSeconds < BlinkCloseSeconds)
            {
                float u = _blinkElapsedSeconds / BlinkCloseSeconds;
                blinkEnvelope = u * u * (3.0f - 2.0f * u);
            }
            else if (_blinkElapsedSeconds < BlinkCloseSeconds + BlinkHoldSeconds)
            {
                blinkEnvelope = 1.0f;
            }
            else if (_blinkElapsedSeconds < BlinkTotalSeconds)
            {
                float u = (_blinkElapsedSeconds - BlinkCloseSeconds - BlinkHoldSeconds) / BlinkOpenSeconds;
                float remain = 1.0f - u;
                blinkEnvelope = remain * remain;
            }
            else
            {
                _blinkElapsedSeconds = -1.0f;
                if (_doubleBlinkPending)
                {
                    _doubleBlinkPending = false;
                    _timeUntilNextBlinkSeconds = NextRange(0.14f, 0.22f);
                }
                else
                {
                    _timeUntilNextBlinkSeconds = NextRange(2.4f, 5.8f);
                }
            }
        }
        else
        {
            _timeUntilNextBlinkSeconds -= dt;
            if (_timeUntilNextBlinkSeconds <= 0.0f)
            {
                _doubleBlinkPending = NextFloat01() < 0.14f;
                StartBlink();
            }
        }

        // Upper eyelids naturally follow downward gaze slightly so looking down stays expressive.
        float gazeLidDroop = Math.Clamp(-_currentPitch * 0.16f, 0.0f, 0.12f);
        float weight = Math.Clamp(MathF.Max(blinkEnvelope, gazeLidDroop), 0.0f, 1.0f);
        if (MathF.Abs(weight - _lastAppliedBlinkWeight) > 1.0e-4f)
            ApplyBlinkWeightToBindings(weight);
    }

    private void StartBlink()
    {
        _blinkElapsedSeconds = 0.0f;
        _timeSinceLastBlinkSeconds = 0.0f;
    }

    private void EnsureBlinkBindingsDiscovered(float dt)
    {
        if (_blinkScanCompleted)
            return;

        _blinkRescanCooldownSeconds -= dt;
        if (_blinkRescanCooldownSeconds > 0.0f)
            return;

        _blinkRescanCooldownSeconds = BlinkRescanIntervalSeconds;
        _blinkBindings.Clear();

        SceneNode? root = ResolveHumanoid()?.SceneNode ?? SceneNode;
        if (root is null)
            return;

        root.IterateComponents<ModelComponent>(ScanModelForBlinkBlendshapes, iterateChildHierarchy: true);
        if (_blinkBindings.Count > 0)
            _blinkScanCompleted = true;
    }

    private void ScanModelForBlinkBlendshapes(ModelComponent model)
    {
        IEnumerable<XRMeshRenderer> renderers = model.GetAllRenderersWhere(
            static renderer => renderer?.Mesh is { HasBlendshapes: true });

        // Prefer separate left/right eyelid shapes when authored; fall back to combined blink.
        for (int i = 0; i < BilateralBlinkCandidates.Length; i++)
        {
            (string leftName, string rightName) = BilateralBlinkCandidates[i];
            if (HasBlendshape(renderers, leftName) && HasBlendshape(renderers, rightName))
            {
                _blinkBindings.Add(new BlinkBinding(model, leftName));
                _blinkBindings.Add(new BlinkBinding(model, rightName));
                return;
            }
        }

        for (int i = 0; i < CombinedBlinkCandidates.Length; i++)
        {
            string combinedName = CombinedBlinkCandidates[i];
            if (HasBlendshape(renderers, combinedName))
            {
                _blinkBindings.Add(new BlinkBinding(model, combinedName));
                return;
            }
        }
    }

    private static bool HasBlendshape(IEnumerable<XRMeshRenderer> renderers, string name)
    {
        foreach (XRMeshRenderer renderer in renderers)
        {
            if (renderer?.Mesh is { HasBlendshapes: true } && renderer.GetBlendshapeIndex(name, out _))
                return true;
        }

        return false;
    }

    private void ApplyBlinkWeightToBindings(float weight)
    {
        _lastAppliedBlinkWeight = weight;
        for (int i = 0; i < _blinkBindings.Count; i++)
        {
            BlinkBinding binding = _blinkBindings[i];
            binding.Model.SetBlendShapeWeightNormalized(binding.BlendshapeName, weight);
        }
    }

    private bool TryComputeCameraGlance(
        Vector3 headPos,
        Vector3 headForward,
        Vector3 headLeft,
        Vector3 headUp,
        out float gazeYaw,
        out float gazePitch,
        out float distance)
    {
        gazeYaw = 0.0f;
        gazePitch = 0.0f;
        distance = 0.0f;

        TransformBase? cam = _viewerCameraTransform;
        if (cam is null)
            return false;

        Vector3 toCam = cam.WorldTranslation - headPos;
        float distSq = toCam.LengthSquared();
        if (!float.IsFinite(distSq) || distSq < 0.04f || distSq > 64.0f)
            return false;

        distance = MathF.Sqrt(distSq);
        Vector3 dir = toCam / distance;

        float fwd = Vector3.Dot(dir, headForward);
        if (fwd < 0.55f)
            return false;

        float left = Vector3.Dot(dir, headLeft);
        float up = Vector3.Dot(dir, headUp);
        float yawRadians = MathF.Atan2(left, fwd);
        float pitchRadians = MathF.Asin(Math.Clamp(up, -1.0f, 1.0f));

        // Only glance at the camera when it sits naturally inside comfortable eye-turn limits.
        if (MathF.Abs(yawRadians) > 0.95f || MathF.Abs(pitchRadians) > 0.52f)
            return false;

        gazeYaw = Math.Clamp(yawRadians / 0.42f, -0.78f, 0.78f);
        gazePitch = Math.Clamp(pitchRadians / 0.28f, -0.60f, 0.55f);
        return true;
    }

    private void ResolveHeadBasis(
        HumanoidComponent humanoid,
        out Vector3 headPos,
        out Vector3 headForward,
        out Vector3 headLeft,
        out Vector3 headUp)
    {
        Vector3 localForward = Vector3.UnitZ;
        Vector3 localUp = Vector3.UnitY;
        TransformBase? headTransform = null;

        if (humanoid.TryGetCompiledAvatarDefinition(out CompiledHumanoidAvatarDefinition compiled))
        {
            if (IsFiniteNonZero(compiled.BodyAxes.Forward))
                localForward = Vector3.Normalize(compiled.BodyAxes.Forward);
            if (IsFiniteNonZero(compiled.BodyAxes.Up))
                localUp = Vector3.Normalize(compiled.BodyAxes.Up);
            headTransform = compiled.GetNode(EHumanoidAvatarBoneRole.Head)?.Transform;
        }

        TransformBase? basisTransform = headTransform ?? _avatarFacingTransform ?? humanoid.SceneNode?.Transform ?? Transform;
        if (basisTransform is null)
        {
            headPos = Vector3.Zero;
            headForward = Vector3.UnitZ;
            headUp = Vector3.UnitY;
            headLeft = Vector3.UnitX;
            return;
        }

        headPos = basisTransform.WorldTranslation;
        Quaternion worldRot = basisTransform.WorldRotation;
        if (headTransform is not null)
        {
            // Head bone local axes vary across authored rigs; prefer the avatar root facing yaw
            // combined with world up for reliable turn-angle and camera-visibility tests.
            TransformBase? rootFacing = _avatarFacingTransform ?? humanoid.SceneNode?.Transform;
            if (rootFacing is not null)
                worldRot = rootFacing.WorldRotation;
        }

        Vector3 fwd = Vector3.Transform(localForward, worldRot);
        Vector3 up = Vector3.Transform(localUp, worldRot);
        headForward = IsFiniteNonZero(fwd) ? Vector3.Normalize(fwd) : Vector3.UnitZ;
        headUp = IsFiniteNonZero(up) ? Vector3.Normalize(up) : Vector3.UnitY;
        Vector3 left = Vector3.Cross(headUp, headForward);
        headLeft = IsFiniteNonZero(left) ? Vector3.Normalize(left) : Vector3.UnitX;
    }

    private void ReadLocomotionSignals(
        out Vector2 moveWorldXZ,
        out int styleIndex,
        out bool isGrounded,
        out float verticalVelocity)
    {
        moveWorldXZ = Vector2.Zero;
        styleIndex = 0;
        isGrounded = true;
        verticalVelocity = 0.0f;

        AnimStateMachineComponent? animator = ResolveAnimator();
        var variables = animator?.StateMachine?.Variables;
        if (variables is null)
            return;

        if (!string.IsNullOrEmpty(_moveXParameter)
            && variables.TryGetValue(_moveXParameter, out var mxVar)
            && mxVar is AnimFloat mx)
            moveWorldXZ.X = float.IsFinite(mx.FloatValue) ? mx.FloatValue : 0.0f;

        if (!string.IsNullOrEmpty(_moveYParameter)
            && variables.TryGetValue(_moveYParameter, out var myVar)
            && myVar is AnimFloat my)
            moveWorldXZ.Y = float.IsFinite(my.FloatValue) ? my.FloatValue : 0.0f;

        if (!string.IsNullOrEmpty(_styleParameter)
            && variables.TryGetValue(_styleParameter, out var styleVar)
            && styleVar is AnimInt style)
            styleIndex = style.IntValue;

        if (!string.IsNullOrEmpty(_isGroundedParameter)
            && variables.TryGetValue(_isGroundedParameter, out var groundedVar)
            && groundedVar is AnimBool grounded)
            isGrounded = grounded.BoolValue;

        if (!string.IsNullOrEmpty(_verticalVelocityParameter)
            && variables.TryGetValue(_verticalVelocityParameter, out var vyVar)
            && vyVar is AnimFloat vy)
            verticalVelocity = float.IsFinite(vy.FloatValue) ? vy.FloatValue : 0.0f;
    }

    private HumanoidComponent? ResolveHumanoid()
        => _humanoid ?? (TryGetSiblingComponent<HumanoidComponent>(out HumanoidComponent? h) ? h : null);

    private AnimStateMachineComponent? ResolveAnimator()
        => _animator ?? (TryGetSiblingComponent<AnimStateMachineComponent>(out AnimStateMachineComponent? a) ? a : null);

    private float NextFloat01()
    {
        uint x = _rngState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rngState = x == 0u ? 0x5EED1234u : x;
        return (_rngState & 0x00FFFFFFu) / 16777216.0f;
    }

    private float NextRange(float min, float max)
        => min + (max - min) * NextFloat01();

    private static bool IsFiniteNonZero(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && v.LengthSquared() > 1.0e-8f;
}

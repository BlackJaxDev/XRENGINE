using System.ComponentModel;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Movement.Modules;
using XREngine.Core.Attributes;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Connects <see cref="AnimStateMachineComponent"/> root motion and steering parameters
/// to <see cref="CharacterMovement3DComponent"/> (<see cref="RootMotionMovementModule"/>)
/// for character avatars controlled by Jolt physics.
/// </summary>
[Category("Gameplay")]
[DisplayName("Character Root Motion Bridge")]
[Description("Routes steering to AnimStateMachineComponent, feeds root-motion deltas into Jolt movement, and reconciles physics collisions back into procedural locomotion motions.")]
public sealed class CharacterRootMotionBridgeComponent : XRComponent
{
    private const float BaseMovementModuleDefaultJumpForce = 5.0f;
    private const float BaseMovementModuleDefaultGravityScale = 5.5f;

    private AnimStateMachineComponent? _animator;
    private CharacterMovement3DComponent? _characterMovement;
    private Transform? _avatarFacingTransform;
    private Transform? _footHeightTransform;
    private string _moveXParameter = "MoveX";
    private string _moveYParameter = "MoveY";
    private string _desiredFacingYawParameter = "DesiredFacingYaw";
    private string _strafeModeParameter = "StrafeMode";
    private string _styleParameter = "LocomotionStyle";
    private string _isGroundedParameter = "IsGrounded";
    private string _verticalVelocityParameter = "VerticalVelocity";
    private string _sprintHeldParameter = "SprintHeld";
    private bool _applyYawDeltaToFacingTransform = true;
    private bool _autoConfigureMovementModule = true;
    private Quaternion _currentFacingRotation = Quaternion.Identity;
    private Quaternion _anchorRootMotionYaw = Quaternion.Identity;
    private float _lastAppliedHalfHeight = float.NaN;
    private float _configuredWorldRootMotionScale = 1.0f;
    private bool _hasPhysicsGroundedFeedback;

    private AnimStateMachineComponent? _subscribedAnimator;
    private CharacterMovement3DComponent? _subscribedMovement;
    private PhysicsMotionBricksTrajectoryProber? _trajectoryProber;
    private IProceduralLocomotionMotion? _lastConfiguredCurrentMotion;
    private object? _lastConfiguredCurrentDriverToken;
    private IProceduralLocomotionMotion? _lastConfiguredNextMotion;
    private object? _lastConfiguredNextDriverToken;

    public AnimStateMachineComponent? Animator
    {
        get => _animator;
        set => SetField(ref _animator, value);
    }

    public CharacterMovement3DComponent? CharacterMovement
    {
        get => _characterMovement;
        set => SetField(ref _characterMovement, value);
    }

    /// <summary>
    /// Optional visual avatar transform that receives animation root yaw deltas,
    /// kept independent from the camera pivot.
    /// </summary>
    public Transform? AvatarFacingTransform
    {
        get => _avatarFacingTransform;
        set => SetField(ref _avatarFacingTransform, value);
    }

    /// <summary>
    /// Optional foot anchor transform whose local Y offset tracks <c>-CharacterMovement.HalfHeight</c>
    /// so avatar feet stay planted on the floor when the capsule resizes for crouch or prone.
    /// </summary>
    public Transform? FootHeightTransform
    {
        get => _footHeightTransform;
        set => SetField(ref _footHeightTransform, value);
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

    public string VerticalVelocityParameter
    {
        get => _verticalVelocityParameter;
        set => SetField(ref _verticalVelocityParameter, value ?? string.Empty);
    }

    public string SprintHeldParameter
    {
        get => _sprintHeldParameter;
        set => SetField(ref _sprintHeldParameter, value ?? string.Empty);
    }

    public bool ApplyYawDeltaToFacingTransform
    {
        get => _applyYawDeltaToFacingTransform;
        set => SetField(ref _applyYawDeltaToFacingTransform, value);
    }

    /// <summary>
    /// When true, <see cref="BindEndpoints"/> assigns a <see cref="RootMotionMovementModule"/>
    /// if <see cref="CharacterMovement"/> does not already use one, preserving any customized
    /// jump force or gravity scale from the previous module.
    /// </summary>
    public bool AutoConfigureMovementModule
    {
        get => _autoConfigureMovementModule;
        set => SetField(ref _autoConfigureMovementModule, value);
    }

    public Quaternion CurrentFacingRotation => _currentFacingRotation;
    public CharacterMovementPhysicsStepReport LastPhysicsReport { get; private set; }

    /// <summary>
    /// Rotates <see cref="AvatarFacingTransform"/> around world Y so the avatar's authored
    /// forward axis faces <paramref name="worldForward"/> before calling <see cref="BindEndpoints"/>.
    /// </summary>
    public void AlignAvatarToWorldForward(Vector3 worldForward)
    {
        if (_avatarFacingTransform is null || !IsFinite(worldForward))
            return;

        float desiredLenSq = worldForward.X * worldForward.X + worldForward.Z * worldForward.Z;
        if (desiredLenSq <= 1.0e-8f)
            return;

        AnimStateMachineComponent? animator = _subscribedAnimator ?? ResolveAnimator();
        Vector3 avatarLocalForward = ResolveAvatarLocalForward(animator);
        float desiredYaw = MathF.Atan2(worldForward.X, worldForward.Z);
        float localForwardYaw = MathF.Atan2(avatarLocalForward.X, avatarLocalForward.Z);
        Quaternion aligned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, desiredYaw - localForwardYaw);
        _avatarFacingTransform.Rotation = aligned;
        _currentFacingRotation = aligned;
    }

    /// <summary>
    /// Attaches event subscriptions to the resolved animator and character movement components
    /// and configures all procedural locomotion motions across every state-machine layer.
    /// Called automatically on activation, and safe to call directly in headless tests.
    /// </summary>
    public void BindEndpoints()
    {
        UnbindEndpoints();

        AnimStateMachineComponent? animator = ResolveAnimator();
        if (animator is not null)
        {
            animator.RootMotionApplicationMode = EHumanoidRootMotionApplicationMode.ExternalConsumer;
            animator.RootMotionEvaluated += OnRootMotionEvaluated;
            _subscribedAnimator = animator;
        }

        CharacterMovement3DComponent? movement = ResolveCharacterMovement();
        if (movement is not null)
        {
            if (_autoConfigureMovementModule && movement.MovementModule is not RootMotionMovementModule)
            {
                var rootMotionModule = new RootMotionMovementModule("Root Motion");
                if (movement.MovementModule is { } previous)
                {
                    if (float.IsFinite(previous.JumpForce)
                        && MathF.Abs(previous.JumpForce - BaseMovementModuleDefaultJumpForce) > 1.0e-4f)
                    {
                        rootMotionModule.JumpForce = previous.JumpForce;
                    }

                    if (float.IsFinite(previous.GravityScale)
                        && MathF.Abs(previous.GravityScale - BaseMovementModuleDefaultGravityScale) > 1.0e-4f)
                    {
                        rootMotionModule.GravityScale = previous.GravityScale;
                    }
                }

                movement.MovementModule = rootMotionModule;
            }

            // Tick root-motion and jump submission directly in PrePhysics so Jolt consumes each
            // fixed step immediately with zero motion-buffer FIFO lag.
            movement.TickInputWithPhysics = true;
            movement.PhysicsStepCompleted += OnPhysicsStepCompleted;
            movement.ControllerResized += OnControllerResized;
            _subscribedMovement = movement;
        }

        if (_avatarFacingTransform is not null && IsFiniteNonZero(_avatarFacingTransform.Rotation))
            _currentFacingRotation = Quaternion.Normalize(_avatarFacingTransform.Rotation);
        else
            _currentFacingRotation = Quaternion.Identity;

        float initialWorldYaw = ComputeCurrentWorldFacingYaw(animator);
        _anchorRootMotionYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, initialWorldYaw);
        _lastAppliedHalfHeight = float.NaN;
        _hasPhysicsGroundedFeedback = false;

        if (animator is not null)
        {
            _configuredWorldRootMotionScale = animator.Humanoid?.EstimateAnimatedMotionScale() ?? 1.0f;
            _trajectoryProber ??= new PhysicsMotionBricksTrajectoryProber(() => World as IRuntimePhysicsWorldContext);
            ConfigureAllLayerProceduralMotions(animator, initialWorldYaw, _configuredWorldRootMotionScale, _trajectoryProber);
        }

        SyncFootHeight(movement);
    }

    /// <summary>
    /// Detaches all event subscriptions and clears physics trajectory probers without mutating scene transforms.
    /// </summary>
    public void UnbindEndpoints()
    {
        if (_subscribedAnimator is not null)
        {
            DetachAllLayerTrajectoryProbers(_subscribedAnimator);
            _subscribedAnimator.RootMotionEvaluated -= OnRootMotionEvaluated;
            _subscribedAnimator = null;
        }

        if (_subscribedMovement is not null)
        {
            _subscribedMovement.PhysicsStepCompleted -= OnPhysicsStepCompleted;
            _subscribedMovement.ControllerResized -= OnControllerResized;
            _subscribedMovement = null;
        }

        _hasPhysicsGroundedFeedback = false;
        _lastConfiguredCurrentMotion = null;
        _lastConfiguredCurrentDriverToken = null;
        _lastConfiguredNextMotion = null;
        _lastConfiguredNextDriverToken = null;
    }

    /// <summary>
    /// Writes a camera-relative steering snapshot into the bound
    /// <see cref="AnimStateMachineComponent"/> parameters.
    /// </summary>
    public void ApplySteering(in SteeringSnapshot steering)
    {
        SyncFootHeight(_subscribedMovement ?? ResolveCharacterMovement());

        AnimStateMachineComponent? animator = _subscribedAnimator ?? ResolveAnimator();
        if (animator is null)
            return;

        if (!string.IsNullOrEmpty(_moveXParameter))
            animator.SetFloat(_moveXParameter, steering.DesiredWorldMoveXZ.X);
        if (!string.IsNullOrEmpty(_moveYParameter))
            animator.SetFloat(_moveYParameter, steering.DesiredWorldMoveXZ.Y);
        if (!string.IsNullOrEmpty(_desiredFacingYawParameter))
            animator.SetFloat(_desiredFacingYawParameter, steering.CameraYawRadians);
        if (!string.IsNullOrEmpty(_strafeModeParameter))
            animator.SetBool(_strafeModeParameter, steering.StrafeMode);
        if (!string.IsNullOrEmpty(_styleParameter))
            animator.SetInt(_styleParameter, (int)steering.LocomotionStyle);
        // Once Jolt physics feedback is active, OnPhysicsStepCompleted owns IsGrounded as the single writer.
        if (!_hasPhysicsGroundedFeedback && !string.IsNullOrEmpty(_isGroundedParameter))
            animator.SetBool(_isGroundedParameter, steering.IsGrounded);
        if (!string.IsNullOrEmpty(_verticalVelocityParameter))
            animator.SetFloat(_verticalVelocityParameter, steering.VerticalVelocity);
        if (!string.IsNullOrEmpty(_sprintHeldParameter))
            animator.SetBool(_sprintHeldParameter, steering.SprintHeld);
    }

    /// <summary>
    /// Value-parameter overload suitable for direct subscription to
    /// <c>CharacterPawnComponent.RootMotionSteeringUpdated</c>.
    /// </summary>
    public void ApplySteeringSnapshot(SteeringSnapshot steering)
        => ApplySteering(in steering);

    /// <summary>
    /// Applies a body-part <see cref="EHumanoidBodyMask"/> to a layer on the bound
    /// <see cref="AnimStateMachineComponent"/> (for example restricting Layer 0 to
    /// <see cref="EHumanoidBodyMask.LowerBody"/> during VR upper-body tracking).
    /// </summary>
    public void ConfigureLayerAvatarMask(int layerIndex, EHumanoidBodyMask mask)
    {
        AnimStateMachineComponent? animator = _subscribedAnimator ?? ResolveAnimator();
        if (animator is null || (uint)layerIndex >= (uint)animator.StateMachine.Layers.Count)
            return;

        if (animator.StateMachine.Layers[layerIndex] is { } layer)
            layer.AvatarMask = mask;
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        BindEndpoints();
    }

    protected override void OnComponentDeactivated()
    {
        UnbindEndpoints();
        base.OnComponentDeactivated();
    }

    private void OnRootMotionEvaluated(HumanoidProjectedRootPose pose, HumanoidRootMotionDelta delta)
    {
        _ = pose;
        AnimStateMachineComponent? animator = _subscribedAnimator ?? ResolveAnimator();
        if (animator is not null)
            EnsureActiveProceduralMotionsConfigured(animator);

        CharacterMovement3DComponent? movement = _subscribedMovement ?? ResolveCharacterMovement();
        if (movement?.MovementModule is not RootMotionMovementModule rootMotionModule)
            return;

        Vector3 localTranslation = IsFinite(delta.Translation) ? delta.Translation : Vector3.Zero;
        Quaternion yawDelta = IsFiniteNonZero(delta.Rotation)
            ? ExtractYawQuaternion(Quaternion.Normalize(delta.Rotation))
            : Quaternion.Identity;

        // Projected root translation deltas live in the fixed canonical-epoch frame
        // (with canonicalYaw removed by HumanoidComponent.CalculateProjectedRootPose),
        // never in the running _currentFacingRotation frame.
        Quaternion canonicalYaw = ResolveCanonicalRootYaw(animator);
        Vector3 worldTranslation = Vector3.Transform(localTranslation, canonicalYaw);
        float animDeltaSeconds = animator?.LastEvaluatedDeltaSeconds ?? 0.0f;
        rootMotionModule.AccumulateRootMotionDelta(worldTranslation, yawDelta, animDeltaSeconds);

        if (TryResolveAbsoluteProceduralFacingRotation(animator, out Quaternion absoluteFacing))
        {
            _currentFacingRotation = absoluteFacing;
            if (_applyYawDeltaToFacingTransform && _avatarFacingTransform is not null)
                _avatarFacingTransform.Rotation = _currentFacingRotation;
        }
        else if (yawDelta != Quaternion.Identity)
        {
            _currentFacingRotation = Quaternion.Normalize(_currentFacingRotation * yawDelta);
            if (_applyYawDeltaToFacingTransform && _avatarFacingTransform is not null)
                _avatarFacingTransform.Rotation = _currentFacingRotation;
        }
    }

    private void OnPhysicsStepCompleted(CharacterMovementPhysicsStepReport report)
    {
        LastPhysicsReport = report;
        _hasPhysicsGroundedFeedback = true;

        AnimStateMachineComponent? animator = _subscribedAnimator ?? ResolveAnimator();
        if (animator is null)
            return;

        EnsureActiveProceduralMotionsConfigured(animator);

        if (!string.IsNullOrEmpty(_isGroundedParameter))
            animator.SetBool(_isGroundedParameter, report.IsGrounded);

        // Reconcile active procedural locomotion motions with Jolt's resolved capsule position and wall blocks.
        AnimStateMachine stateMachine = animator.StateMachine;
        for (int layerIdx = 0; layerIdx < stateMachine.Layers.Count; layerIdx++)
        {
            AnimLayer? layer = stateMachine.Layers[layerIdx];
            if (layer is null)
                continue;

            ReconcileStateMotion(layer.CurrentState, in report);
            if (!ReferenceEquals(layer.NextState, layer.CurrentState))
                ReconcileStateMotion(layer.NextState, in report);
        }
    }

    private void OnControllerResized(float halfHeight)
        => SyncFootHeight(_subscribedMovement ?? ResolveCharacterMovement());

    private void SyncFootHeight(CharacterMovement3DComponent? movement)
    {
        if (_footHeightTransform is null || movement is null)
            return;

        float halfHeight = movement.HalfHeight;
        if (!float.IsFinite(halfHeight) || MathF.Abs(halfHeight - _lastAppliedHalfHeight) <= 1.0e-4f)
            return;

        _lastAppliedHalfHeight = halfHeight;
        Vector3 translation = _footHeightTransform.Translation;
        _footHeightTransform.Translation = new Vector3(translation.X, -halfHeight, translation.Z);
    }

    private float ComputeCurrentWorldFacingYaw(AnimStateMachineComponent? animator)
    {
        // Align procedural world-space yaw and fixed root-motion anchor yaw with the avatar's
        // actual world-space forward vector (Unity prefabs face -Z at identity).
        Vector3 avatarLocalForward = ResolveAvatarLocalForward(animator);
        Vector3 worldForward = Vector3.Transform(avatarLocalForward, _currentFacingRotation);
        float forwardXZLenSq = worldForward.X * worldForward.X + worldForward.Z * worldForward.Z;
        return forwardXZLenSq > 1.0e-8f
            ? MathF.Atan2(worldForward.X, worldForward.Z)
            : 0.0f;
    }

    private void EnsureActiveProceduralMotionsConfigured(AnimStateMachineComponent animator)
    {
        AnimStateMachine stateMachine = animator.StateMachine;
        for (int layerIdx = 0; layerIdx < stateMachine.Layers.Count; layerIdx++)
        {
            AnimLayer? layer = stateMachine.Layers[layerIdx];
            if (layer is null)
                continue;

            if (layer.CurrentState?.Motion is IProceduralLocomotionMotion currentMotion)
            {
                object? token = currentMotion.RuntimeDriverToken;
                if (!ReferenceEquals(currentMotion, _lastConfiguredCurrentMotion)
                    || !ReferenceEquals(token, _lastConfiguredCurrentDriverToken))
                {
                    ConfigureSingleMotion(animator, currentMotion);
                    _lastConfiguredCurrentMotion = currentMotion;
                    _lastConfiguredCurrentDriverToken = currentMotion.RuntimeDriverToken;
                }
            }

            if (!ReferenceEquals(layer.NextState, layer.CurrentState)
                && layer.NextState?.Motion is IProceduralLocomotionMotion nextMotion)
            {
                object? token = nextMotion.RuntimeDriverToken;
                if (!ReferenceEquals(nextMotion, _lastConfiguredNextMotion)
                    || !ReferenceEquals(token, _lastConfiguredNextDriverToken))
                {
                    ConfigureSingleMotion(animator, nextMotion);
                    _lastConfiguredNextMotion = nextMotion;
                    _lastConfiguredNextDriverToken = nextMotion.RuntimeDriverToken;
                }
            }
        }
    }

    private void ConfigureSingleMotion(AnimStateMachineComponent animator, IProceduralLocomotionMotion motion)
    {
        float currentWorldYaw = ComputeCurrentWorldFacingYaw(animator);
        _configuredWorldRootMotionScale = animator.Humanoid?.EstimateAnimatedMotionScale() ?? _configuredWorldRootMotionScale;
        _trajectoryProber ??= new PhysicsMotionBricksTrajectoryProber(() => World as IRuntimePhysicsWorldContext);
        motion.ConfigureLocomotionRuntime(currentWorldYaw, _configuredWorldRootMotionScale, _trajectoryProber);
    }

    private void ConfigureAllLayerProceduralMotions(
        AnimStateMachineComponent animator,
        float initialWorldYaw,
        float worldRootMotionScale,
        IMotionBricksTrajectoryProber? prober)
    {
        AnimStateMachine stateMachine = animator.StateMachine;
        for (int layerIdx = 0; layerIdx < stateMachine.Layers.Count; layerIdx++)
        {
            AnimLayer? layer = stateMachine.Layers[layerIdx];
            if (layer is null)
                continue;

            for (int stateIdx = 0; stateIdx < layer.States.Count; stateIdx++)
            {
                if (layer.States[stateIdx]?.Motion is IProceduralLocomotionMotion motion)
                    motion.ConfigureLocomotionRuntime(initialWorldYaw, worldRootMotionScale, prober);
            }

            if (layer.CurrentState?.Motion is IProceduralLocomotionMotion currentMotion)
            {
                currentMotion.ConfigureLocomotionRuntime(initialWorldYaw, worldRootMotionScale, prober);
                _lastConfiguredCurrentMotion = currentMotion;
                _lastConfiguredCurrentDriverToken = currentMotion.RuntimeDriverToken;
            }

            if (!ReferenceEquals(layer.NextState, layer.CurrentState)
                && layer.NextState?.Motion is IProceduralLocomotionMotion nextMotion)
            {
                nextMotion.ConfigureLocomotionRuntime(initialWorldYaw, worldRootMotionScale, prober);
                _lastConfiguredNextMotion = nextMotion;
                _lastConfiguredNextDriverToken = nextMotion.RuntimeDriverToken;
            }
        }
    }

    private static void DetachAllLayerTrajectoryProbers(AnimStateMachineComponent animator)
    {
        AnimStateMachine stateMachine = animator.StateMachine;
        for (int layerIdx = 0; layerIdx < stateMachine.Layers.Count; layerIdx++)
        {
            AnimLayer? layer = stateMachine.Layers[layerIdx];
            if (layer is null)
                continue;

            for (int stateIdx = 0; stateIdx < layer.States.Count; stateIdx++)
            {
                if (layer.States[stateIdx]?.Motion is IProceduralLocomotionMotion motion)
                    motion.DetachTrajectoryProber();
            }

            if (layer.CurrentState?.Motion is IProceduralLocomotionMotion currentMotion)
                currentMotion.DetachTrajectoryProber();

            if (!ReferenceEquals(layer.NextState, layer.CurrentState)
                && layer.NextState?.Motion is IProceduralLocomotionMotion nextMotion)
                nextMotion.DetachTrajectoryProber();
        }
    }

    private static Vector3 ResolveAvatarLocalForward(AnimStateMachineComponent? animator)
    {
        HumanoidComponent? humanoid = animator?.Humanoid;
        if (humanoid is not null && humanoid.TryGetCompiledAvatarDefinition(out CompiledHumanoidAvatarDefinition compiled))
        {
            Vector3 forward = compiled.BodyAxes.Forward;
            if (IsFinite(forward) && (forward.X * forward.X + forward.Z * forward.Z) > 1.0e-8f)
                return Vector3.Normalize(new Vector3(forward.X, 0.0f, forward.Z));
        }

        return Vector3.UnitZ;
    }

    private Quaternion ResolveCanonicalRootYaw(AnimStateMachineComponent? animator)
    {
        if (animator is not null)
        {
            AnimStateMachine stateMachine = animator.StateMachine;
            for (int layerIdx = 0; layerIdx < stateMachine.Layers.Count; layerIdx++)
            {
                AnimLayer? layer = stateMachine.Layers[layerIdx];
                if (layer?.CurrentState?.Motion is IProceduralLocomotionMotion motion
                    && motion.TryGetCanonicalBodyRotation(out Quaternion canonicalBodyRotation)
                    && IsFiniteNonZero(canonicalBodyRotation))
                {
                    return ExtractYawQuaternion(Quaternion.Normalize(canonicalBodyRotation));
                }
            }
        }

        return _anchorRootMotionYaw;
    }

    private static bool TryResolveAbsoluteProceduralFacingRotation(
        AnimStateMachineComponent? animator,
        out Quaternion facingRotation)
    {
        facingRotation = Quaternion.Identity;
        if (animator is null)
            return false;

        AnimStateMachine stateMachine = animator.StateMachine;
        for (int layerIdx = 0; layerIdx < stateMachine.Layers.Count; layerIdx++)
        {
            AnimLayer? layer = stateMachine.Layers[layerIdx];
            if (layer?.CurrentState?.Motion is IProceduralLocomotionMotion motion
                && motion.TryGetCurrentBodyRotation(out Quaternion currentBodyRotation)
                && IsFiniteNonZero(currentBodyRotation))
            {
                if (!TryExtractFacingYaw(Quaternion.Normalize(currentBodyRotation), out float currentWorldYaw))
                    return false;

                Vector3 avatarLocalForward = ResolveAvatarLocalForward(animator);
                float localForwardYaw = MathF.Atan2(avatarLocalForward.X, avatarLocalForward.Z);
                facingRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, currentWorldYaw - localForwardYaw);
                return true;
            }
        }

        return false;
    }

    private static void ReconcileStateMotion(AnimState? state, in CharacterMovementPhysicsStepReport report)
    {
        if (state?.Motion is IProceduralLocomotionMotion motion)
        {
            motion.ApplyPhysicsRootReconciliation(
                report.FootPosition,
                report.ActualPlanarVelocity,
                report.WallNormal,
                report.IsBlocked);
        }
    }

    private AnimStateMachineComponent? ResolveAnimator()
        => _animator ?? (SceneNode?.GetComponentInHierarchy(nameof(AnimStateMachineComponent)) as AnimStateMachineComponent);

    private CharacterMovement3DComponent? ResolveCharacterMovement()
        => _characterMovement ?? SceneNode?.GetComponent<CharacterMovement3DComponent>();

    private static bool TryExtractFacingYaw(Quaternion rotation, out float yawRadians)
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);
        float forwardLenSq = forward.X * forward.X + forward.Z * forward.Z;
        if (forwardLenSq >= 0.25f)
        {
            yawRadians = MathF.Atan2(forward.X, forward.Z);
            return true;
        }

        // At steep forward pitch (such as prone), local +Z points near vertical so extract yaw
        // from local +X crossed with world +Y, which stays perpendicular to pitch rotation.
        Vector3 right = Vector3.Transform(Vector3.UnitX, rotation);
        Vector3 planarForward = Vector3.Cross(right, Vector3.UnitY);
        float planarLenSq = planarForward.X * planarForward.X + planarForward.Z * planarForward.Z;
        if (planarLenSq > 1.0e-8f)
        {
            yawRadians = MathF.Atan2(planarForward.X, planarForward.Z);
            return true;
        }

        if (forwardLenSq > 1.0e-8f)
        {
            yawRadians = MathF.Atan2(forward.X, forward.Z);
            return true;
        }

        yawRadians = 0.0f;
        return false;
    }

    private static Quaternion ExtractYawQuaternion(Quaternion rotation)
        => TryExtractFacingYaw(rotation, out float yaw)
            ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)
            : Quaternion.Identity;

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFiniteNonZero(Quaternion value)
        => float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && float.IsFinite(value.W)
        && value.LengthSquared() > 1.0e-8f;
}

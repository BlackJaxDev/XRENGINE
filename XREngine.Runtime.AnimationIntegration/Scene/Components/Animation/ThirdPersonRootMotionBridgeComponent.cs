using System.ComponentModel;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Movement.Modules;
using XREngine.Core.Attributes;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Connects <see cref="AnimStateMachineComponent"/> root motion and steering parameters
/// to <see cref="CharacterMovement3DComponent"/> (<see cref="RootMotionMovementModule"/>)
/// for third-person avatars controlled by Jolt physics.
/// </summary>
[Category("Gameplay")]
[DisplayName("Third-Person Root Motion Bridge")]
[Description("Routes steering to AnimStateMachineComponent, feeds root-motion deltas into Jolt movement, and reconciles physics collisions back into MotionBricks.")]
public sealed class ThirdPersonRootMotionBridgeComponent : XRComponent
{
    private AnimStateMachineComponent? _animator;
    private CharacterMovement3DComponent? _characterMovement;
    private Transform? _avatarFacingTransform;
    private string _moveXParameter = "MoveX";
    private string _moveYParameter = "MoveY";
    private string _desiredFacingYawParameter = "DesiredFacingYaw";
    private string _strafeModeParameter = "StrafeMode";
    private string _isGroundedParameter = "IsGrounded";
    private bool _applyYawDeltaToFacingTransform = true;
    private Quaternion _currentFacingRotation = Quaternion.Identity;

    private AnimStateMachineComponent? _subscribedAnimator;
    private CharacterMovement3DComponent? _subscribedMovement;

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
    /// kept independent from a third-person orbit camera pivot.
    /// </summary>
    public Transform? AvatarFacingTransform
    {
        get => _avatarFacingTransform;
        set => SetField(ref _avatarFacingTransform, value);
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

    public string IsGroundedParameter
    {
        get => _isGroundedParameter;
        set => SetField(ref _isGroundedParameter, value ?? string.Empty);
    }

    public bool ApplyYawDeltaToFacingTransform
    {
        get => _applyYawDeltaToFacingTransform;
        set => SetField(ref _applyYawDeltaToFacingTransform, value);
    }

    public Quaternion CurrentFacingRotation => _currentFacingRotation;
    public CharacterMovementPhysicsStepReport LastPhysicsReport { get; private set; }

    /// <summary>
    /// Explicitly attaches subscriptions to the resolved animator and character movement components.
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
            if (movement.MovementModule is not RootMotionMovementModule)
                movement.MovementModule = new RootMotionMovementModule("Root Motion");

            movement.PhysicsStepCompleted += OnPhysicsStepCompleted;
            _subscribedMovement = movement;
        }

        if (_avatarFacingTransform is not null && IsFiniteNonZero(_avatarFacingTransform.Rotation))
            _currentFacingRotation = Quaternion.Normalize(_avatarFacingTransform.Rotation);
    }

    /// <summary>
    /// Detaches all event subscriptions without mutating scene transforms.
    /// </summary>
    public void UnbindEndpoints()
    {
        if (_subscribedAnimator is not null)
        {
            _subscribedAnimator.RootMotionEvaluated -= OnRootMotionEvaluated;
            _subscribedAnimator = null;
        }

        if (_subscribedMovement is not null)
        {
            _subscribedMovement.PhysicsStepCompleted -= OnPhysicsStepCompleted;
            _subscribedMovement = null;
        }
    }

    /// <summary>
    /// Writes a third-person camera-relative steering snapshot into the bound
    /// <see cref="AnimStateMachineComponent"/> parameters.
    /// </summary>
    public void ApplySteering(in ThirdPersonSteeringSnapshot steering)
    {
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
        if (!string.IsNullOrEmpty(_isGroundedParameter))
            animator.SetBool(_isGroundedParameter, steering.IsGrounded);
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
        CharacterMovement3DComponent? movement = _subscribedMovement ?? ResolveCharacterMovement();
        if (movement?.MovementModule is not RootMotionMovementModule rootMotionModule)
            return;

        Vector3 localTranslation = IsFinite(delta.Translation) ? delta.Translation : Vector3.Zero;
        Quaternion yawDelta = IsFiniteNonZero(delta.Rotation)
            ? ExtractYawQuaternion(Quaternion.Normalize(delta.Rotation))
            : Quaternion.Identity;

        Vector3 worldTranslation = Vector3.Transform(localTranslation, _currentFacingRotation);
        rootMotionModule.AccumulateRootMotionDelta(worldTranslation, yawDelta);

        if (yawDelta != Quaternion.Identity)
        {
            _currentFacingRotation = Quaternion.Normalize(_currentFacingRotation * yawDelta);
            if (_applyYawDeltaToFacingTransform && _avatarFacingTransform is not null)
                _avatarFacingTransform.Rotation = _currentFacingRotation;
        }
    }

    private void OnPhysicsStepCompleted(CharacterMovementPhysicsStepReport report)
    {
        LastPhysicsReport = report;

        AnimStateMachineComponent? animator = _subscribedAnimator ?? ResolveAnimator();
        if (animator is null)
            return;

        if (!string.IsNullOrEmpty(_isGroundedParameter))
            animator.SetBool(_isGroundedParameter, report.IsGrounded);

        // Reconcile active MotionBricks drivers with Jolt's resolved capsule position and wall blocks.
        AnimStateMachine stateMachine = animator.StateMachine;
        for (int layerIdx = 0; layerIdx < stateMachine.Layers.Count; layerIdx++)
        {
            AnimLayer? layer = stateMachine.Layers[layerIdx];
            if (layer is null)
                continue;

            ReconcileStateDriver(layer.CurrentState, report);
            if (!ReferenceEquals(layer.NextState, layer.CurrentState))
                ReconcileStateDriver(layer.NextState, report);
        }
    }

    private static void ReconcileStateDriver(AnimState? state, in CharacterMovementPhysicsStepReport report)
    {
        if (state?.Motion is MotionBricksLocomotionMotion motionBricks && motionBricks.ActiveDriver is { } driver)
        {
            driver.ApplyPhysicsRootReconciliation(
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

    private static Quaternion ExtractYawQuaternion(Quaternion rotation)
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);
        float lenSq = forward.X * forward.X + forward.Z * forward.Z;
        if (lenSq <= 1.0e-8f)
            return Quaternion.Identity;

        float yaw = MathF.Atan2(forward.X, forward.Z);
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFiniteNonZero(Quaternion value)
        => float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && float.IsFinite(value.W)
        && value.LengthSquared() > 1.0e-8f;
}

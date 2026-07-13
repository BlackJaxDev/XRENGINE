using System.Numerics;
using XREngine.Components.Physics;

namespace XREngine.Scene.Physics;

/// <summary>
/// Backend-neutral rigid-body construction request. The selected backend translates the
/// authored shapes and material into native objects.
/// </summary>
public readonly record struct PhysicsRigidBodyCreateInfo(
    IReadOnlyList<PhysicsColliderShape> ColliderShapes,
    IPhysicsGeometry? FallbackGeometry,
    AbstractPhysicsMaterial? RuntimeMaterial,
    PhysicsMaterialDefinition? MaterialDefinition,
    (Vector3 position, Quaternion rotation) Pose,
    Vector3 FallbackShapeOffsetTranslation,
    Quaternion FallbackShapeOffsetRotation,
    float Density,
    LayerMask LayerMask)
{
    public bool GravityEnabled { get; init; } = true;
    public float MaxLinearVelocity { get; init; } = 100.0f;
    public float MaxAngularVelocity { get; init; } = 100.0f;
    public PhysicsSolverIterations SolverIterations { get; init; } = PhysicsSolverIterations.Default;
    public PhysicsRigidBodyFlags BodyFlags { get; init; } = PhysicsRigidBodyFlags.None;
    public PhysicsLockFlags LockFlags { get; init; } = PhysicsLockFlags.None;
}

/// <summary>
/// Backend-neutral capsule-controller construction request.
/// </summary>
public readonly record struct PhysicsCharacterControllerCreateInfo(
    Vector3 Position,
    Vector3 UpDirection,
    float Radius,
    float Height,
    float SlopeLimit,
    float ContactOffset,
    float StepOffset,
    float Density,
    PhysicsMaterialDefinition? MaterialDefinition)
{
    public float InvisibleWallHeight { get; init; }
    public float MaxJumpHeight { get; init; } = 1.0f;
    public float ScaleCoefficient { get; init; } = 0.8f;
    public float VolumeGrowth { get; init; } = 1.5f;
    public bool SlideOnSteepSlopes { get; init; }
    public bool ConstrainedClimbing { get; init; }
}

/// <summary>
/// Small construction boundary implemented by each physics backend. Gameplay components
/// depend on this service instead of branching on concrete scene types.
/// </summary>
public interface IPhysicsBackendService
{
    IAbstractStaticRigidBody? CreateStaticRigidBody(in PhysicsRigidBodyCreateInfo createInfo);
    IAbstractDynamicRigidBody? CreateDynamicRigidBody(in PhysicsRigidBodyCreateInfo createInfo);
    IAbstractCharacterController? CreateCharacterController(in PhysicsCharacterControllerCreateInfo createInfo);
    bool TryReplaceCollisionShapes(
        IAbstractRigidPhysicsActor actor,
        in PhysicsRigidBodyCreateInfo createInfo);
}

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
    float TotalHeight,
    float SlopeLimit,
    float ContactOffset,
    float StepOffset,
    float Density,
    PhysicsMaterialDefinition? MaterialDefinition)
{
    public CharacterMotionInputModel MotionInputModel { get; init; } = CharacterMotionInputModel.Velocity;
    public LayerMask CollisionLayerMask { get; init; } = LayerMask.Everything;
    public float PredictiveContactDistance { get; init; } = 0.1f;
    public float CollisionTolerance { get; init; } = 0.001f;
    public float StickToFloorDistance { get; init; } = 0.1f;
    public float StepDownExtra { get; init; }
    public float MaxStrength { get; init; } = 100.0f;
    public float InvisibleWallHeight { get; init; }
    public float MaxJumpHeight { get; init; } = 1.0f;
    public float ScaleCoefficient { get; init; } = 0.8f;
    public float VolumeGrowth { get; init; } = 1.5f;
    public bool SlideOnSteepSlopes { get; init; }
    public bool ConstrainedClimbing { get; init; }
}

internal static class PhysicsCharacterControllerCreateInfoValidator
{
    public static void Validate(in PhysicsCharacterControllerCreateInfo createInfo)
    {
        if (!float.IsFinite(createInfo.Radius) || createInfo.Radius <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(createInfo.Radius), "Character capsule radius must be finite and greater than zero.");
        if (!float.IsFinite(createInfo.TotalHeight) || createInfo.TotalHeight < 2.0f * createInfo.Radius)
            throw new ArgumentOutOfRangeException(nameof(createInfo.TotalHeight), "Character capsule total height must be finite and at least twice its radius.");
        if (!IsFinite(createInfo.Position))
            throw new ArgumentException("Character position must contain only finite values.", nameof(createInfo.Position));
        if (!IsFinite(createInfo.UpDirection) || createInfo.UpDirection.LengthSquared() < 1e-8f)
            throw new ArgumentException("Character up direction must be finite and non-zero.", nameof(createInfo.UpDirection));
        if (!float.IsFinite(createInfo.SlopeLimit) || createInfo.SlopeLimit < -1.0f || createInfo.SlopeLimit > 1.0f)
            throw new ArgumentOutOfRangeException(nameof(createInfo.SlopeLimit), "Character slope cosine must be finite and in [-1, 1].");

        ValidateNonNegative(createInfo.ContactOffset, nameof(createInfo.ContactOffset));
        ValidateNonNegative(createInfo.StepOffset, nameof(createInfo.StepOffset));
        ValidateNonNegative(createInfo.Density, nameof(createInfo.Density));
        ValidateNonNegative(createInfo.PredictiveContactDistance, nameof(createInfo.PredictiveContactDistance));
        if (!float.IsFinite(createInfo.CollisionTolerance) || createInfo.CollisionTolerance <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(createInfo.CollisionTolerance), "Character collision tolerance must be finite and greater than zero.");
        ValidateNonNegative(createInfo.StickToFloorDistance, nameof(createInfo.StickToFloorDistance));
        ValidateNonNegative(createInfo.StepDownExtra, nameof(createInfo.StepDownExtra));
        ValidateNonNegative(createInfo.MaxStrength, nameof(createInfo.MaxStrength));
    }

    private static void ValidateNonNegative(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0.0f)
            throw new ArgumentOutOfRangeException(name, $"Character setting {name} must be finite and non-negative.");
    }

    private static bool IsFinite(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// Small construction boundary implemented by each physics backend. Gameplay components
/// depend on this service instead of branching on concrete scene types.
/// </summary>
public interface IPhysicsBackendService
{
    PhysicsCharacterControllerCapabilities CharacterControllerCapabilities { get; }
    IAbstractStaticRigidBody? CreateStaticRigidBody(in PhysicsRigidBodyCreateInfo createInfo);
    IAbstractDynamicRigidBody? CreateDynamicRigidBody(in PhysicsRigidBodyCreateInfo createInfo);
    IAbstractCharacterController? CreateCharacterController(in PhysicsCharacterControllerCreateInfo createInfo);
    bool TryReplaceCollisionShapes(
        IAbstractRigidPhysicsActor actor,
        in PhysicsRigidBodyCreateInfo createInfo);
}

namespace XREngine.Scene.Physics.Jolt;

/// <summary>
/// Explicit Jolt construction adapter. Jolt-native objects remain behind neutral contracts.
/// </summary>
internal sealed class JoltBackendService(JoltScene scene) : IPhysicsBackendService
{
    public PhysicsCharacterControllerCapabilities CharacterControllerCapabilities
        => PhysicsCharacterControllerCapabilities.DisplacementInput
            | PhysicsCharacterControllerCapabilities.VelocityInput
            | PhysicsCharacterControllerCapabilities.ArbitraryUp
            | PhysicsCharacterControllerCapabilities.MovingGround
            | PhysicsCharacterControllerCapabilities.DynamicBodyInteraction
            | PhysicsCharacterControllerCapabilities.MaximumStrength
            | PhysicsCharacterControllerCapabilities.PredictiveContacts
            | PhysicsCharacterControllerCapabilities.IndependentCollisionTolerance
            | PhysicsCharacterControllerCapabilities.FloorStickDistance
            | PhysicsCharacterControllerCapabilities.IndependentStepDown
            | PhysicsCharacterControllerCapabilities.SteepSlopeSliding
            | PhysicsCharacterControllerCapabilities.CollisionFiltering;

    public IAbstractStaticRigidBody? CreateStaticRigidBody(in PhysicsRigidBodyCreateInfo createInfo)
        => scene.CreateStaticRigidBody(in createInfo);

    public IAbstractDynamicRigidBody? CreateDynamicRigidBody(in PhysicsRigidBodyCreateInfo createInfo)
        => scene.CreateDynamicRigidBody(in createInfo);

    public IAbstractCharacterController? CreateCharacterController(
        in PhysicsCharacterControllerCreateInfo createInfo)
    {
        PhysicsCharacterControllerCreateInfoValidator.Validate(in createInfo);
        var controller = new JoltCharacterVirtualController(scene, createInfo.Position, createInfo.UpDirection)
        {
            Radius = createInfo.Radius,
            TotalHeight = createInfo.TotalHeight,
            ContactOffset = createInfo.ContactOffset,
            PredictiveContactDistance = createInfo.PredictiveContactDistance,
            CollisionTolerance = createInfo.CollisionTolerance,
            StickToFloorDistance = createInfo.StickToFloorDistance,
            StepDownExtra = createInfo.StepDownExtra,
            StepOffset = createInfo.StepOffset,
            SlopeLimit = createInfo.SlopeLimit,
            MotionInputModel = createInfo.MotionInputModel,
            CollisionLayerMask = createInfo.CollisionLayerMask,
            Mass = CalculateCapsuleMass(createInfo.Radius, createInfo.TotalHeight, createInfo.Density),
            MaxStrength = createInfo.MaxStrength,
            SlideOnSteepSlopes = createInfo.SlideOnSteepSlopes,
        };
        return controller;
    }

    private static float CalculateCapsuleMass(float radius, float totalHeight, float density)
    {
        float safeRadius = MathF.Max(0.001f, radius);
        float cylinderHeight = MathF.Max(0.0f, totalHeight - 2.0f * safeRadius);
        float cylinderVolume = MathF.PI * safeRadius * safeRadius * cylinderHeight;
        float sphereVolume = 4.0f / 3.0f * MathF.PI * safeRadius * safeRadius * safeRadius;
        return MathF.Max(0.001f, MathF.Max(0.0f, density) * (cylinderVolume + sphereVolume));
    }

    public bool TryReplaceCollisionShapes(
        IAbstractRigidPhysicsActor actor,
        in PhysicsRigidBodyCreateInfo createInfo)
        => actor is JoltRigidActor joltActor
            && scene.TryReplaceCollisionShapes(joltActor, in createInfo);
}

namespace XREngine.Scene.Physics.Jolt;

/// <summary>
/// Explicit Jolt construction adapter. Jolt-native objects remain behind neutral contracts.
/// </summary>
internal sealed class JoltBackendService(JoltScene scene) : IPhysicsBackendService
{
    public IAbstractStaticRigidBody? CreateStaticRigidBody(in PhysicsRigidBodyCreateInfo createInfo)
        => scene.CreateStaticRigidBody(in createInfo);

    public IAbstractDynamicRigidBody? CreateDynamicRigidBody(in PhysicsRigidBodyCreateInfo createInfo)
        => scene.CreateDynamicRigidBody(in createInfo);

    public IAbstractCharacterController? CreateCharacterController(
        in PhysicsCharacterControllerCreateInfo createInfo)
    {
        var controller = new JoltCharacterVirtualController(scene, createInfo.Position)
        {
            Radius = createInfo.Radius,
            Height = createInfo.Height,
            ContactOffset = createInfo.ContactOffset,
            StepOffset = createInfo.StepOffset,
            SlopeLimit = createInfo.SlopeLimit,
            UpDirection = createInfo.UpDirection,
        };
        return controller;
    }

    public bool TryReplaceCollisionShapes(
        IAbstractRigidPhysicsActor actor,
        in PhysicsRigidBodyCreateInfo createInfo)
        => actor is JoltRigidActor joltActor
            && scene.TryReplaceCollisionShapes(joltActor, in createInfo);
}

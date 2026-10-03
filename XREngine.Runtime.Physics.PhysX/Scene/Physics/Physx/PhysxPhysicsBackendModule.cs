namespace XREngine.Scene.Physics.Physx;

/// <summary>Creates PhysX scenes for applications that install the Windows backend.</summary>
public sealed class PhysxPhysicsBackendModule : IPhysicsBackendModule
{
    public EPhysicsLibrary Id => EPhysicsLibrary.PhysX;
    public string DisplayName => "PhysX";
    public PhysicsBackendPlatforms SupportedPlatforms => PhysicsBackendPlatforms.Windows;
    public PhysicsBackendCapabilities Capabilities => PhysicsBackendCapabilities.RigidBodies |
        PhysicsBackendCapabilities.Queries | PhysicsBackendCapabilities.Joints |
        PhysicsBackendCapabilities.CharacterControllers | PhysicsBackendCapabilities.DebugFrames |
        PhysicsBackendCapabilities.GpuDynamics | PhysicsBackendCapabilities.HeightFields;

    public AbstractPhysicsScene CreateScene() => new PhysxScene();
}

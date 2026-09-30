namespace XREngine.Scene.Physics.Jolt;

/// <summary>Creates Jolt scenes for applications that install the Jolt backend.</summary>
public sealed class JoltPhysicsBackendModule : IPhysicsBackendModule
{
    public EPhysicsLibrary Id => EPhysicsLibrary.Jolt;
    public string DisplayName => "Jolt";
    public PhysicsBackendPlatforms SupportedPlatforms => PhysicsBackendPlatforms.Windows | PhysicsBackendPlatforms.Linux | PhysicsBackendPlatforms.MacOS;
    public PhysicsBackendCapabilities Capabilities => PhysicsBackendCapabilities.RigidBodies |
        PhysicsBackendCapabilities.Queries | PhysicsBackendCapabilities.Joints |
        PhysicsBackendCapabilities.CharacterControllers | PhysicsBackendCapabilities.DebugFrames |
        PhysicsBackendCapabilities.HeightFields;

    public AbstractPhysicsScene CreateScene()
    {
        JoltBootstrap.EnsureInitialized();
        return new JoltScene();
    }
}

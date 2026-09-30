namespace XREngine.Scene.Physics.Jitter2;

/// <summary>Creates experimental Jitter scenes when explicitly installed.</summary>
public sealed class JitterPhysicsBackendModule : IPhysicsBackendModule
{
    public EPhysicsLibrary Id => EPhysicsLibrary.Jitter;
    public string DisplayName => "Jitter (experimental)";
    public PhysicsBackendPlatforms SupportedPlatforms => PhysicsBackendPlatforms.Windows | PhysicsBackendPlatforms.Linux | PhysicsBackendPlatforms.MacOS;
    public PhysicsBackendCapabilities Capabilities => PhysicsBackendCapabilities.RigidBodies | PhysicsBackendCapabilities.Queries;

    public AbstractPhysicsScene CreateScene() => new JitterScene();
}

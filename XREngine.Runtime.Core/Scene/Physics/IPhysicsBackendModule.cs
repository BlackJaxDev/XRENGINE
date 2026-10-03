namespace XREngine.Scene.Physics;

/// <summary>
/// Describes and constructs one statically installed physics implementation.
/// </summary>
public interface IPhysicsBackendModule
{
    EPhysicsLibrary Id { get; }
    string DisplayName { get; }
    PhysicsBackendPlatforms SupportedPlatforms { get; }
    PhysicsBackendCapabilities Capabilities { get; }
    AbstractPhysicsScene CreateScene();
}

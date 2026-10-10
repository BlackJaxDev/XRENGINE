namespace XREngine.Scene.Physics.Jolt;

/// <summary>Creates Jolt scenes for applications that install the Jolt backend.</summary>
public sealed class JoltPhysicsBackendModule : IPhysicsBackendModule
{
#if XRE_JOLT_BROWSER
    private const bool BrowserSourceSupply = true;
#else
    private const bool BrowserSourceSupply = false;
#endif
    public EPhysicsLibrary Id => EPhysicsLibrary.Jolt;
    public string DisplayName => "Jolt";
    public PhysicsBackendPlatforms SupportedPlatforms => BrowserSourceSupply
        ? PhysicsBackendPlatforms.Browser
        : PhysicsBackendPlatforms.Windows | PhysicsBackendPlatforms.Linux | PhysicsBackendPlatforms.MacOS;
    public PhysicsBackendCapabilities Capabilities => PhysicsBackendCapabilities.RigidBodies |
        PhysicsBackendCapabilities.Queries | PhysicsBackendCapabilities.Joints |
        PhysicsBackendCapabilities.CharacterControllers | PhysicsBackendCapabilities.DebugFrames |
        PhysicsBackendCapabilities.HeightFields;

    public AbstractPhysicsScene CreateScene()
    {
        if (OperatingSystem.IsBrowser() && !BrowserSourceSupply)
            throw new PlatformNotSupportedException("Browser Jolt requires the reviewed browser source supply; the desktop package cannot initialize a browser scene.");
        JoltBootstrap.EnsureInitialized();
        return new JoltScene();
    }
}

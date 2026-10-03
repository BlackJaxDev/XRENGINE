namespace XREngine.Scene.Physics;

/// <summary>Platforms on which a physics module can create scenes.</summary>
[Flags]
public enum PhysicsBackendPlatforms
{
    None = 0,
    Windows = 1,
    Linux = 2,
    MacOS = 4,
    Browser = 8,
}

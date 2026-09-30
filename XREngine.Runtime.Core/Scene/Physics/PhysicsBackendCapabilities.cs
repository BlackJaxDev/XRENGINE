namespace XREngine.Scene.Physics;

/// <summary>Features supplied by a registered physics backend.</summary>
[Flags]
public enum PhysicsBackendCapabilities
{
    None = 0,
    RigidBodies = 1,
    Queries = 2,
    Joints = 4,
    CharacterControllers = 8,
    DebugFrames = 16,
    GpuDynamics = 32,
    HeightFields = 64,
}

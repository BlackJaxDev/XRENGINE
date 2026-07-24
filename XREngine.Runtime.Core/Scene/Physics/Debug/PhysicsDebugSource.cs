namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Identifies the physics backend that produced a debug visualization frame.
/// </summary>
public enum PhysicsDebugSource : byte
{
    None,
    PhysX,
    Jolt,
}

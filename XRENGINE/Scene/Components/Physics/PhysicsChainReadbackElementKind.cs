namespace XREngine.Components;

/// <summary>
/// Concrete packed output type emitted by a selective readback gather.
/// </summary>
public enum PhysicsChainReadbackElementKind : byte
{
    Particle,
    Bone,
    Socket,
    Bounds,
    CollisionEvent,
    FullTransform,
}

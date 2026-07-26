namespace XREngine.Components;

[Flags]
public enum PhysicsChainReadbackFields : ushort
{
    None = 0,
    Particles = 1 << 0,
    Bones = 1 << 1,
    Sockets = 1 << 2,
    Bounds = 1 << 3,
    CollisionEvents = 1 << 4,
    FullTransformMirror = 1 << 5,
}

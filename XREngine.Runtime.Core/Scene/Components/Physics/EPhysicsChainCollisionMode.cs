namespace XREngine.Components;

/// <summary>
/// Selects which part of a chain collides with colliders.
/// </summary>
public enum EPhysicsChainCollisionMode
{
    /// <summary>Each particle collides as a sphere. A collider can pass between two particles.</summary>
    Particle,
    /// <summary>
    /// Each segment collides along its full length as a capsule, from the parent particle to the child particle.
    /// A collider that hits the segment turns it around the parent.
    /// </summary>
    Segment,
}

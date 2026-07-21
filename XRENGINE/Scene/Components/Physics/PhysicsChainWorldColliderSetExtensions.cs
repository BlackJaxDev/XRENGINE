namespace XREngine.Components;

/// <summary>
/// Structural-boundary access to per-world shared collider resources.
/// </summary>
internal static class PhysicsChainWorldColliderSetExtensions
{
    public static PhysicsChainColliderSet GetOrCreateColliderSet(
        this PhysicsChainWorld world,
        ReadOnlySpan<PhysicsChainColliderShape> shapes)
        => PhysicsChainColliderSetCache.ForWorld(world).GetOrAdd(shapes);

    public static int GetUniqueColliderSetCount(this PhysicsChainWorld world)
        => PhysicsChainColliderSetCache.ForWorld(world).UniqueSetCount;

    public static PhysicsChainColliderSetCacheSnapshot GetColliderSetCacheSnapshot(this PhysicsChainWorld world)
        => PhysicsChainColliderSetCache.ForWorld(world).GetSnapshot();
}

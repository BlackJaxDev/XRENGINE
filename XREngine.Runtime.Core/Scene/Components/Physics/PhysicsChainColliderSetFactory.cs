namespace XREngine.Components;

/// <summary>Creates immutable shared collider resources from authored shapes.</summary>
public static class PhysicsChainColliderSetFactory
{
    public static PhysicsChainColliderSet Create(
        long stableId,
        ReadOnlySpan<PhysicsChainColliderShape> shapes,
        ulong contentHash)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stableId, 1L);
        return new PhysicsChainColliderSet(stableId, shapes.ToArray(), contentHash);
    }
}

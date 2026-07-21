namespace XREngine.Components;

/// <summary>
/// Explicit failure raised when a structural allocation exceeds a configured
/// arena contract. Callers may grow/rebuild out of band; data is never dropped.
/// </summary>
public sealed class PhysicsChainArenaCapacityException : InvalidOperationException
{
    public PhysicsChainArenaCapacityException(int maximumCapacity)
        : base($"The physics-chain arena reached its configured capacity of {maximumCapacity} slots.")
        => MaximumCapacity = maximumCapacity;

    public int MaximumCapacity { get; }
}

namespace XREngine.Components;

/// <summary>
/// Immutable, content-addressed collider topology and shape stream shared by
/// any number of chain instances. Poses are intentionally not part of this
/// resource, allowing them to use an independently versioned dirty stream.
/// </summary>
public sealed class PhysicsChainColliderSet
{
    private readonly PhysicsChainColliderShape[] _shapes;

    internal PhysicsChainColliderSet(long stableId, PhysicsChainColliderShape[] shapes, ulong contentHash)
    {
        StableId = stableId;
        _shapes = shapes;
        ContentHash = contentHash;
    }

    public long StableId { get; }
    public uint ShapeVersion => 1u;
    public ulong ContentHash { get; }
    public ReadOnlyMemory<PhysicsChainColliderShape> Shapes => _shapes;

    internal bool ContentEquals(ReadOnlySpan<PhysicsChainColliderShape> shapes)
        => _shapes.AsSpan().SequenceEqual(shapes);
}

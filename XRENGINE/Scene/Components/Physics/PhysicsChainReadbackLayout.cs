namespace XREngine.Components;

/// <summary>
/// Stable byte layout shared by backend gather implementations and CPU
/// delivery consumers. Elements are written consecutively without padding.
/// </summary>
public static class PhysicsChainReadbackLayout
{
    public const int ParticleByteCount = 24;
    public const int AffineTransformByteCount = 48;
    public const int BoundsByteCount = 24;
    public const int CollisionEventByteCount = 40;

    private static readonly PhysicsChainReadbackElementKind[] ParticleKinds = [PhysicsChainReadbackElementKind.Particle];
    private static readonly PhysicsChainReadbackElementKind[] BoneKinds = [PhysicsChainReadbackElementKind.Bone];
    private static readonly PhysicsChainReadbackElementKind[] SocketKinds = [PhysicsChainReadbackElementKind.Socket];
    private static readonly PhysicsChainReadbackElementKind[] BoundsKinds = [PhysicsChainReadbackElementKind.Bounds];
    private static readonly PhysicsChainReadbackElementKind[] EventKinds = [PhysicsChainReadbackElementKind.CollisionEvent];
    private static readonly PhysicsChainReadbackElementKind[] MirrorKinds = [PhysicsChainReadbackElementKind.FullTransform];

    public static int GetElementByteCount(PhysicsChainReadbackElementKind kind)
        => kind switch
        {
            PhysicsChainReadbackElementKind.Particle => ParticleByteCount,
            PhysicsChainReadbackElementKind.Bone => AffineTransformByteCount,
            PhysicsChainReadbackElementKind.Socket => AffineTransformByteCount,
            PhysicsChainReadbackElementKind.Bounds => BoundsByteCount,
            PhysicsChainReadbackElementKind.CollisionEvent => CollisionEventByteCount,
            PhysicsChainReadbackElementKind.FullTransform => AffineTransformByteCount,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public static bool TryCalculate(
        PhysicsChainReadbackFields fields,
        int selectedElementCount,
        out int elementCount,
        out int byteCount)
    {
        elementCount = 0;
        byteCount = 0;
        if (selectedElementCount <= 0)
            return false;

        ReadOnlySpan<PhysicsChainReadbackElementKind> kinds = GetKinds(fields);
        try
        {
            elementCount = checked(kinds.Length * selectedElementCount);
            int bytesPerSelection = 0;
            for (int i = 0; i < kinds.Length; ++i)
                bytesPerSelection = checked(bytesPerSelection + GetElementByteCount(kinds[i]));
            byteCount = checked(bytesPerSelection * selectedElementCount);
            return elementCount > 0 && byteCount > 0;
        }
        catch (OverflowException)
        {
            elementCount = 0;
            byteCount = 0;
            return false;
        }
    }

    internal static ReadOnlySpan<PhysicsChainReadbackElementKind> GetKinds(
        PhysicsChainReadbackFields fields)
        => fields switch
        {
            PhysicsChainReadbackFields.Particles => ParticleKinds,
            PhysicsChainReadbackFields.Bones => BoneKinds,
            PhysicsChainReadbackFields.Sockets => SocketKinds,
            PhysicsChainReadbackFields.Bounds => BoundsKinds,
            PhysicsChainReadbackFields.CollisionEvents => EventKinds,
            PhysicsChainReadbackFields.FullTransformMirror => MirrorKinds,
            _ => BuildKinds(fields),
        };

    private static PhysicsChainReadbackElementKind[] BuildKinds(PhysicsChainReadbackFields fields)
    {
        var kinds = new List<PhysicsChainReadbackElementKind>(6);
        if ((fields & PhysicsChainReadbackFields.Particles) != 0)
            kinds.Add(PhysicsChainReadbackElementKind.Particle);
        if ((fields & PhysicsChainReadbackFields.Bones) != 0)
            kinds.Add(PhysicsChainReadbackElementKind.Bone);
        if ((fields & PhysicsChainReadbackFields.Sockets) != 0)
            kinds.Add(PhysicsChainReadbackElementKind.Socket);
        if ((fields & PhysicsChainReadbackFields.Bounds) != 0)
            kinds.Add(PhysicsChainReadbackElementKind.Bounds);
        if ((fields & PhysicsChainReadbackFields.CollisionEvents) != 0)
            kinds.Add(PhysicsChainReadbackElementKind.CollisionEvent);
        if ((fields & PhysicsChainReadbackFields.FullTransformMirror) != 0)
            kinds.Add(PhysicsChainReadbackElementKind.FullTransform);
        return [.. kinds];
    }
}

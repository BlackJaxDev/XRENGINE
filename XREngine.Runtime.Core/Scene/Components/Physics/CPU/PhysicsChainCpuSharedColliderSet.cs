using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// CPU collision view over one shared authored collider set. World-space
/// collider records update at the dynamic boundary and larger sets use the
/// shared BVH to build compact candidate spans without allocation.
/// </summary>
public sealed class PhysicsChainCpuSharedColliderSet
{
    private readonly PhysicsChainCpuCollider[] _worldColliders;
    private long _queryCount;
    private long _smallSetBypassCount;
    private long _candidateCount;
    private long _fullSetFallbackCount;

    public PhysicsChainCpuSharedColliderSet(PhysicsChainColliderSet colliderSet)
    {
        RuntimeSet = new PhysicsChainColliderRuntimeSet(colliderSet);
        _worldColliders = new PhysicsChainCpuCollider[colliderSet.Shapes.Length];
        for (int colliderIndex = 0; colliderIndex < _worldColliders.Length; ++colliderIndex)
            _worldColliders[colliderIndex] = ToWorldCollider(
                colliderSet.Shapes.Span[colliderIndex],
                Matrix4x4.Identity);
    }

    public PhysicsChainColliderRuntimeSet RuntimeSet { get; }
    public int ColliderCount => _worldColliders.Length;
    internal ReadOnlySpan<PhysicsChainCpuCollider> WorldColliders => _worldColliders;
    public int RequiredTraversalStackLength => RuntimeSet.RequiredTraversalStackLength;

    public bool TrySetPose(int colliderIndex, in Matrix4x4 pose)
    {
        if (!RuntimeSet.TrySetPose(colliderIndex, pose))
            return false;
        _worldColliders[colliderIndex] = ToWorldCollider(
            RuntimeSet.ColliderSet.Shapes.Span[colliderIndex],
            pose);
        return true;
    }

    /// <summary>
    /// Writes exact candidates into caller-owned storage. Zero through four
    /// colliders bypass the BVH. Overflow conservatively writes the full set
    /// when capacity permits and never exposes a truncated collision set.
    /// </summary>
    public bool TryBuildCandidates(
        in PhysicsChainAabb sweptChainBounds,
        Span<PhysicsChainCpuCollider> destination,
        Span<int> candidateIndices,
        Span<int> traversalStack,
        out int candidateCount,
        out bool usedFullSetFallback)
    {
        candidateCount = 0;
        usedFullSetFallback = false;
        Interlocked.Increment(ref _queryCount);

        if (_worldColliders.Length <= 4)
        {
            if (destination.Length < _worldColliders.Length)
                return false;
            _worldColliders.CopyTo(destination);
            candidateCount = _worldColliders.Length;
            Interlocked.Increment(ref _smallSetBypassCount);
            Interlocked.Add(ref _candidateCount, candidateCount);
            return true;
        }

        PhysicsChainColliderCandidateQueryResult result = RuntimeSet.QueryCandidates(
            sweptChainBounds,
            candidateIndices,
            traversalStack);
        if (!result.Succeeded)
        {
            candidateCount = _worldColliders.Length;
            usedFullSetFallback = true;
            Interlocked.Increment(ref _fullSetFallbackCount);
            Interlocked.Add(ref _candidateCount, candidateCount);
            if (destination.Length < _worldColliders.Length)
                return false;
            _worldColliders.CopyTo(destination);
            return true;
        }

        if (destination.Length < result.CandidateCount)
            return false;
        for (int candidateIndex = 0; candidateIndex < result.CandidateCount; ++candidateIndex)
            destination[candidateIndex] = _worldColliders[candidateIndices[candidateIndex]];
        candidateCount = result.CandidateCount;
        Interlocked.Add(ref _candidateCount, candidateCount);
        return true;
    }

    public PhysicsChainCpuSharedColliderSetSnapshot GetSnapshot()
        => new(
            RuntimeSet.ColliderSet.StableId,
            _worldColliders.Length,
            Interlocked.Read(ref _queryCount),
            Interlocked.Read(ref _smallSetBypassCount),
            Interlocked.Read(ref _candidateCount),
            Interlocked.Read(ref _fullSetFallbackCount),
            RuntimeSet.GetSnapshot());

    private static PhysicsChainCpuCollider ToWorldCollider(
        in PhysicsChainColliderShape shape,
        in Matrix4x4 pose)
    {
        Vector3 center = Vector3.Transform(shape.LocalCenter, pose);
        Vector3 basisX = new(pose.M11, pose.M12, pose.M13);
        Vector3 basisY = new(pose.M21, pose.M22, pose.M23);
        Vector3 basisZ = new(pose.M31, pose.M32, pose.M33);
        float scaleX = basisX.Length();
        float scaleY = basisY.Length();
        float scaleZ = basisZ.Length();
        float maximumScale = MathF.Max(scaleX, MathF.Max(scaleY, scaleZ));
        Vector3 axisX = NormalizeOrFallback(basisX, Vector3.UnitX);
        Vector3 axisY = NormalizeOrFallback(basisY, Vector3.UnitY);
        Vector3 axisZ = NormalizeOrFallback(basisZ, Vector3.UnitZ);

        return shape.Kind switch
        {
            PhysicsChainColliderShapeKind.Sphere => PhysicsChainCpuCollider.Sphere(center, shape.Radius * maximumScale),
            PhysicsChainColliderShapeKind.Capsule => CreateCapsule(shape, pose, center, maximumScale),
            PhysicsChainColliderShapeKind.Box => PhysicsChainCpuCollider.Box(
                center,
                axisX,
                axisY,
                axisZ,
                shape.HalfExtents * new Vector3(scaleX, scaleY, scaleZ)),
            PhysicsChainColliderShapeKind.Plane => CreatePlane(shape, pose, center),
            _ => default,
        };
    }

    private static PhysicsChainCpuCollider CreateCapsule(
        in PhysicsChainColliderShape shape,
        in Matrix4x4 pose,
        Vector3 center,
        float maximumScale)
    {
        Vector3 worldAxis = Vector3.TransformNormal(shape.UnitAxis * shape.AxisLength, pose);
        return PhysicsChainCpuCollider.Capsule(
            center - worldAxis,
            center + worldAxis,
            shape.Radius * maximumScale);
    }

    private static PhysicsChainCpuCollider CreatePlane(
        in PhysicsChainColliderShape shape,
        in Matrix4x4 pose,
        Vector3 center)
    {
        Vector3 normal = NormalizeOrFallback(Vector3.TransformNormal(shape.UnitAxis, pose), Vector3.UnitY);
        return PhysicsChainCpuCollider.Plane(normal, -Vector3.Dot(normal, center), inside: false);
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 1e-12f
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }
}

using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// World-owned runtime state for one shared collider topology. Shape data stays
/// immutable while dirty pose ranges refit a shared BVH exactly once before
/// any chain using the set queries it.
/// </summary>
public sealed class PhysicsChainColliderRuntimeSet
{
    private readonly PhysicsChainColliderBroadphase _broadphase;
    private long _refitCount;
    private long _queryCount;
    private long _candidateOverflowCount;
    private long _traversalFailureCount;

    public PhysicsChainColliderRuntimeSet(PhysicsChainColliderSet colliderSet)
    {
        ArgumentNullException.ThrowIfNull(colliderSet);
        ColliderSet = colliderSet;
        Poses = new PhysicsChainColliderPoseBuffer(colliderSet.Shapes.Length);
        _broadphase = new PhysicsChainColliderBroadphase(colliderSet.Shapes.Span);
        if (!TrySynchronizeDirtyPoses())
            throw new InvalidOperationException("The collider set's initial poses could not be synchronized.");
    }

    public PhysicsChainColliderSet ColliderSet { get; }
    public PhysicsChainColliderPoseBuffer Poses { get; }
    public int RequiredTraversalStackLength => _broadphase.NodeCount;

    public bool TrySetPose(int colliderIndex, in Matrix4x4 pose)
        => Poses.TrySetPose(colliderIndex, pose);

    /// <summary>
    /// Applies only the dirty pose range. The method is allocation-free and is
    /// intended for the world's dynamic-update boundary before parallel query.
    /// </summary>
    public bool TrySynchronizeDirtyPoses()
    {
        int dirtyCount = Poses.DirtyCount;
        if (dirtyCount == 0)
            return true;

        int dirtyStart = Poses.DirtyStart;
        if (!_broadphase.UpdatePoseRange(dirtyStart, Poses.Poses.Span.Slice(dirtyStart, dirtyCount)))
            return false;

        Poses.ClearDirtyRange();
        Interlocked.Increment(ref _refitCount);
        return true;
    }

    /// <summary>
    /// Queries candidates for a swept chain bound. Candidate overflow is
    /// explicit: callers must conservatively run the full collider set rather
    /// than accepting the truncated prefix.
    /// </summary>
    public PhysicsChainColliderCandidateQueryResult QueryCandidates(
        in PhysicsChainAabb sweptChainBounds,
        Span<int> candidates,
        Span<int> traversalStack)
    {
        Interlocked.Increment(ref _queryCount);
        if (!TrySynchronizeDirtyPoses())
        {
            Interlocked.Increment(ref _traversalFailureCount);
            return new PhysicsChainColliderCandidateQueryResult(0, ColliderSet.Shapes.Length, false, true);
        }

        PhysicsChainColliderCandidateQueryResult result = _broadphase.Query(
            sweptChainBounds,
            candidates,
            traversalStack);
        if (result.CandidateOverflow)
            Interlocked.Increment(ref _candidateOverflowCount);
        if (result.TraversalOverflow)
            Interlocked.Increment(ref _traversalFailureCount);
        return result;
    }

    public PhysicsChainColliderRuntimeSetSnapshot GetSnapshot()
        => new(
            ColliderSet.StableId,
            ColliderSet.ShapeVersion,
            Poses.PoseVersion,
            ColliderSet.Shapes.Length,
            _broadphase.NodeCount,
            Interlocked.Read(ref _refitCount),
            Interlocked.Read(ref _queryCount),
            Interlocked.Read(ref _candidateOverflowCount),
            Interlocked.Read(ref _traversalFailureCount));
}

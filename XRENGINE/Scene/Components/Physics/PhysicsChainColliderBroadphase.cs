using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Shared-set BVH. Shape topology is built once; ordinary pose updates refit
/// leaves and ancestors without allocation. Plane colliders are explicit
/// always-candidates because they are unbounded.
/// </summary>
public sealed class PhysicsChainColliderBroadphase
{
    private struct Node
    {
        public PhysicsChainAabb Bounds;
        public int Left;
        public int Right;
        public int ColliderIndex;
        public readonly bool IsLeaf => ColliderIndex >= 0;
    }

    private readonly PhysicsChainColliderShape[] _shapes;
    private readonly PhysicsChainAabb[] _worldBounds;
    private readonly int[] _boundedColliderIndices;
    private readonly int[] _planeColliderIndices;
    private readonly Node[] _nodes;
    private int _nextNode;

    public PhysicsChainColliderBroadphase(ReadOnlySpan<PhysicsChainColliderShape> shapes)
    {
        _shapes = shapes.ToArray();
        _worldBounds = new PhysicsChainAabb[shapes.Length];

        int planeCount = 0;
        for (int i = 0; i < shapes.Length; ++i)
            if (shapes[i].Kind == PhysicsChainColliderShapeKind.Plane)
                ++planeCount;

        _planeColliderIndices = new int[planeCount];
        _boundedColliderIndices = new int[shapes.Length - planeCount];
        int plane = 0;
        int bounded = 0;
        for (int i = 0; i < shapes.Length; ++i)
        {
            if (shapes[i].Kind == PhysicsChainColliderShapeKind.Plane)
                _planeColliderIndices[plane++] = i;
            else
                _boundedColliderIndices[bounded++] = i;
        }

        _nodes = new Node[Math.Max(0, _boundedColliderIndices.Length * 2 - 1)];
        Span<Matrix4x4> identityPoses = shapes.Length <= 128
            ? stackalloc Matrix4x4[shapes.Length]
            : new Matrix4x4[shapes.Length];
        identityPoses.Fill(Matrix4x4.Identity);
        UpdatePoses(identityPoses);
        if (_boundedColliderIndices.Length > 0)
            Build(0, _boundedColliderIndices.Length);
    }

    public int ColliderCount => _shapes.Length;
    public int NodeCount => _nodes.Length;
    public int PlaneCount => _planeColliderIndices.Length;

    public bool UpdatePoses(ReadOnlySpan<Matrix4x4> poses)
    {
        if (poses.Length != _shapes.Length)
            return false;

        for (int i = 0; i < poses.Length; ++i)
        {
            if (!IsFinite(poses[i]))
                return false;
            _worldBounds[i] = CalculateWorldBounds(_shapes[i], poses[i]);
        }

        if (_nextNode == 0)
            return true;

        for (int nodeIndex = _nodes.Length - 1; nodeIndex >= 0; --nodeIndex)
        {
            ref Node node = ref _nodes[nodeIndex];
            node.Bounds = node.IsLeaf
                ? _worldBounds[node.ColliderIndex]
                : PhysicsChainAabb.Union(_nodes[node.Left].Bounds, _nodes[node.Right].Bounds);
        }
        return true;
    }

    /// <summary>
    /// Refits a contiguous dirty pose range without recalculating unchanged
    /// collider world bounds. Internal BVH nodes are then refit bottom-up.
    /// </summary>
    public bool UpdatePoseRange(int startIndex, ReadOnlySpan<Matrix4x4> poses)
    {
        if (startIndex < 0 || startIndex > _shapes.Length || poses.Length > _shapes.Length - startIndex)
            return false;

        for (int poseIndex = 0; poseIndex < poses.Length; ++poseIndex)
        {
            Matrix4x4 pose = poses[poseIndex];
            if (!IsFinite(pose))
                return false;
        }

        for (int poseIndex = 0; poseIndex < poses.Length; ++poseIndex)
        {
            int colliderIndex = startIndex + poseIndex;
            _worldBounds[colliderIndex] = CalculateWorldBounds(_shapes[colliderIndex], poses[poseIndex]);
        }

        if (_nextNode == 0 || poses.IsEmpty)
            return true;

        for (int nodeIndex = _nodes.Length - 1; nodeIndex >= 0; --nodeIndex)
        {
            ref Node node = ref _nodes[nodeIndex];
            node.Bounds = node.IsLeaf
                ? _worldBounds[node.ColliderIndex]
                : PhysicsChainAabb.Union(_nodes[node.Left].Bounds, _nodes[node.Right].Bounds);
        }
        return true;
    }

    public PhysicsChainColliderCandidateQueryResult Query(
        in PhysicsChainAabb sweptChainBounds,
        Span<int> candidates,
        Span<int> traversalStack)
    {
        if (!sweptChainBounds.IsValid)
            return new PhysicsChainColliderCandidateQueryResult(0, 0, false, true);
        if (_nodes.Length > 0 && traversalStack.Length < _nodes.Length)
            return new PhysicsChainColliderCandidateQueryResult(0, 0, false, true);

        int written = 0;
        int required = 0;
        for (int i = 0; i < _planeColliderIndices.Length; ++i)
            AddCandidate(_planeColliderIndices[i], candidates, ref written, ref required);

        int stackCount = 0;
        if (_nodes.Length > 0)
            traversalStack[stackCount++] = 0;
        while (stackCount > 0)
        {
            Node node = _nodes[traversalStack[--stackCount]];
            if (!node.Bounds.Intersects(sweptChainBounds))
                continue;
            if (node.IsLeaf)
            {
                AddCandidate(node.ColliderIndex, candidates, ref written, ref required);
                continue;
            }

            traversalStack[stackCount++] = node.Left;
            traversalStack[stackCount++] = node.Right;
        }

        return new PhysicsChainColliderCandidateQueryResult(
            written,
            required,
            required > candidates.Length,
            false);
    }

    private int Build(int start, int count)
    {
        int nodeIndex = _nextNode++;
        if (count == 1)
        {
            int colliderIndex = _boundedColliderIndices[start];
            _nodes[nodeIndex] = new Node
            {
                Bounds = _worldBounds[colliderIndex],
                Left = -1,
                Right = -1,
                ColliderIndex = colliderIndex,
            };
            return nodeIndex;
        }

        PhysicsChainAabb centroidBounds = PhysicsChainAabb.Invalid;
        for (int i = start; i < start + count; ++i)
        {
            Vector3 center = _worldBounds[_boundedColliderIndices[i]].Center;
            centroidBounds = PhysicsChainAabb.Union(centroidBounds, new PhysicsChainAabb(center, center));
        }
        Vector3 extent = centroidBounds.Maximum - centroidBounds.Minimum;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        SortRangeByAxis(start, count, axis);

        int leftCount = count / 2;
        int left = Build(start, leftCount);
        int right = Build(start + leftCount, count - leftCount);
        _nodes[nodeIndex] = new Node
        {
            Bounds = PhysicsChainAabb.Union(_nodes[left].Bounds, _nodes[right].Bounds),
            Left = left,
            Right = right,
            ColliderIndex = -1,
        };
        return nodeIndex;
    }

    private void SortRangeByAxis(int start, int count, int axis)
    {
        int end = start + count;
        for (int i = start + 1; i < end; ++i)
        {
            int value = _boundedColliderIndices[i];
            float coordinate = GetAxis(_worldBounds[value].Center, axis);
            int insertion = i;
            while (insertion > start
                && GetAxis(_worldBounds[_boundedColliderIndices[insertion - 1]].Center, axis) > coordinate)
            {
                _boundedColliderIndices[insertion] = _boundedColliderIndices[insertion - 1];
                --insertion;
            }
            _boundedColliderIndices[insertion] = value;
        }
    }

    private static void AddCandidate(int colliderIndex, Span<int> candidates, ref int written, ref int required)
    {
        if (written < candidates.Length)
            candidates[written++] = colliderIndex;
        ++required;
    }

    private static float GetAxis(Vector3 value, int axis)
        => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;

    private static PhysicsChainAabb CalculateWorldBounds(
        PhysicsChainColliderShape shape,
        in Matrix4x4 pose)
    {
        if (shape.Kind == PhysicsChainColliderShapeKind.Plane)
            return PhysicsChainAabb.Invalid;

        Vector3 localExtents = shape.LocalBoundsExtents;
        Vector3 center = Vector3.Transform(shape.LocalCenter, pose);
        Vector3 worldExtents = new(
            MathF.Abs(pose.M11) * localExtents.X + MathF.Abs(pose.M21) * localExtents.Y + MathF.Abs(pose.M31) * localExtents.Z,
            MathF.Abs(pose.M12) * localExtents.X + MathF.Abs(pose.M22) * localExtents.Y + MathF.Abs(pose.M32) * localExtents.Z,
            MathF.Abs(pose.M13) * localExtents.X + MathF.Abs(pose.M23) * localExtents.Y + MathF.Abs(pose.M33) * localExtents.Z);
        return new PhysicsChainAabb(center - worldExtents, center + worldExtents);
    }

    private static bool IsFinite(in Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
            && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
            && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
            && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

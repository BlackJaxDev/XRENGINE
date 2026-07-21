using System.Numerics;
using SimpleScene.Util.ssBVH;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering;

namespace XREngine.Editor;

public sealed partial class MathBvhTestComponent
{
    private static readonly AABB s_sceneTreeBounds = AABB.FromCenterSize(
        new Vector3(0.0f, 2.5f, 0.0f),
        new Vector3(11.0f, 7.0f, 11.0f));
    private static readonly OctreeNode<SceneBvhItem>.DelIntersectionTest s_sceneIntersectionTest = SceneItemIntersects;

    private CpuBvhRenderTree<SceneBvhItem>? _cpuSceneTree;
    private SceneBvhItem[]? _cpuSceneItems;
    private Action<SceneBvhItem>? _sceneHitCollector;
    private DelRenderAABB? _sceneTreeDebugRenderer;
    private AABB _sceneQueryBounds;
    private int _sceneQueryHitCount;
    private int _sceneMoveCursor;

    private BVH<Triangle>? _cpuMeshTree;
    private List<BVHNode<Triangle>>? _cpuMeshNodes;
    private bool[]? _cpuMeshVisitedNodes;
    private BVH<Triangle>.NodeTest? _cpuMeshNodeTest;
    private Segment _meshQuerySegment;
    private Vector3? _lastMeshHitPoint;

    private void InitializeCpuWorkload()
    {
        if (!_configured)
            return;

        if (Mode == MathBvhTestMode.CpuScene)
            InitializeCpuSceneWorkload();
        else if (Mode == MathBvhTestMode.LegacyCpuMesh)
            InitializeCpuMeshWorkload();
    }

    private void InitializeCpuSceneWorkload()
    {
        AABB[] sourceBounds = CreateScenePrimitiveBounds();
        _cpuSceneItems = new SceneBvhItem[sourceBounds.Length];
        var items = new List<SceneBvhItem>(sourceBounds.Length);
        for (int i = 0; i < sourceBounds.Length; i++)
        {
            var item = new SceneBvhItem(sourceBounds[i]);
            _cpuSceneItems[i] = item;
            items.Add(item);
        }

        _cpuSceneTree = new CpuBvhRenderTree<SceneBvhItem>(
            s_sceneTreeBounds,
            items,
            new CpuBvhOptions { LeafCapacity = (int)SceneLeafCapacity });
        _sceneHitCollector = MarkSceneQueryHit;
        _sceneTreeDebugRenderer = RenderCpuSceneTreeNode;
        Interlocked.Increment(ref _buildOperationCount);
        ValidateCpuSceneQuery((float)Engine.ElapsedTime);
    }

    private void InitializeCpuMeshWorkload()
    {
        if (_sourceTriangles is not { Count: > 0 } triangles)
        {
            SetValidationState(ready: false, passed: false);
            return;
        }

        _cpuMeshTree = new BVH<Triangle>(new TriangleAdapter(), [.. triangles]);
        _cpuMeshNodes = _cpuMeshTree.Traverse(static _ => true);
        _cpuMeshVisitedNodes = new bool[_cpuMeshTree._nodeCount];
        _cpuMeshNodeTest = CpuMeshNodeIntersectsQuery;
        Interlocked.Increment(ref _buildOperationCount);
        ValidateCpuMeshRay((float)Engine.ElapsedTime);
    }

    private void UpdateCpuSceneWorkload()
    {
        if (_cpuSceneTree is null || _cpuSceneItems is null)
            return;

        float time = (float)Engine.ElapsedTime;
        const int movesPerTick = 3;
        for (int move = 0; move < movesPerTick; move++)
        {
            int index = _sceneMoveCursor++ % _cpuSceneItems.Length;
            SceneBvhItem item = _cpuSceneItems[index];
            AABB baseBounds = item.BaseBounds;
            Vector3 offset = new(
                MathF.Sin(time * 0.73f + index * 0.31f) * 0.28f,
                MathF.Cos(time * 0.91f + index * 0.17f) * 0.18f,
                MathF.Sin(time * 0.57f + index * 0.23f) * 0.24f);
            item.LocalCullingVolume = AABB.FromCenterSize(baseBounds.Center + offset, baseBounds.Size);
            item.OctreeNode?.QueueItemMoved(item);
        }

        _cpuSceneTree.Swap();
        Interlocked.Increment(ref _updateOperationCount);
        ValidateCpuSceneQuery(time);
    }

    private void ValidateCpuSceneQuery(float time)
    {
        if (_cpuSceneTree is null || _cpuSceneItems is null || _sceneHitCollector is null)
            return;

        Vector3 center = new(
            MathF.Sin(time * 0.52f) * 3.4f,
            2.5f + MathF.Sin(time * 0.37f) * 0.55f,
            MathF.Cos(time * 0.41f) * 3.4f);
        _sceneQueryBounds = AABB.FromCenterSize(center, new Vector3(3.0f, 2.6f, 3.0f));

        int bruteForceCount = 0;
        for (int i = 0; i < _cpuSceneItems.Length; i++)
        {
            SceneBvhItem item = _cpuSceneItems[i];
            item.QueryHit = false;
            if (item.LocalCullingVolume is { } itemBounds && _sceneQueryBounds.Intersects(itemBounds))
                bruteForceCount++;
        }

        _sceneQueryHitCount = 0;
        _cpuSceneTree.CollectVisible(
            _sceneQueryBounds,
            onlyContainingItems: false,
            _sceneHitCollector,
            s_sceneIntersectionTest);
        Interlocked.Increment(ref _queryOperationCount);
        SetValidationState(
            ready: true,
            passed: _sceneQueryHitCount == bruteForceCount,
            hitCount: _sceneQueryHitCount);
    }

    private void MarkSceneQueryHit(SceneBvhItem item)
    {
        item.QueryHit = true;
        _sceneQueryHitCount++;
    }

    private static bool SceneItemIntersects(SceneBvhItem item, IVolume? volume, bool containsOnly)
    {
        if (item.LocalCullingVolume is not { } itemBounds || volume is null)
            return true;

        EContainment containment = volume.ContainsBox(itemBounds.ToBox(Matrix4x4.Identity));
        return containsOnly
            ? containment == EContainment.Contains
            : containment != EContainment.Disjoint || volume is AABB query && query.Intersects(itemBounds);
    }

    private void UpdateCpuMeshWorkload()
    {
        if (_cpuMeshTree is null)
            return;

        ValidateCpuMeshRay((float)Engine.ElapsedTime);
        Interlocked.Increment(ref _updateOperationCount);
    }

    private void ValidateCpuMeshRay(float time)
    {
        if (_cpuMeshTree is null ||
            _cpuMeshVisitedNodes is null ||
            _cpuMeshNodeTest is null ||
            _sourceTriangles is not { Count: > 0 } triangles)
        {
            return;
        }

        Vector3 start = new(
            MathF.Sin(time * 0.47f) * 3.7f,
            6.0f,
            MathF.Cos(time * 0.39f) * 3.7f);
        Vector3 end = new(start.X + MathF.Sin(time * 0.23f) * 0.7f, -2.0f, start.Z);
        _meshQuerySegment = new Segment(start, end);
        Array.Clear(_cpuMeshVisitedNodes);

        // The legacy API owns and returns this list. Retaining that allocation is intentional:
        // the benchmark should measure the traversal contract that production callers still use.
        List<BVHNode<Triangle>> candidates = _cpuMeshTree.Traverse(_cpuMeshNodeTest);
        float acceleratedDistance = float.PositiveInfinity;
        int acceleratedHitCount = 0;
        Vector3 direction = end - start;
        float length = direction.Length();
        if (length > 1e-6f)
            direction /= length;

        for (int nodeIndex = 0; nodeIndex < candidates.Count; nodeIndex++)
        {
            BVHNode<Triangle> node = candidates[nodeIndex];
            if ((uint)node.nodeNumber < (uint)_cpuMeshVisitedNodes.Length)
                _cpuMeshVisitedNodes[node.nodeNumber] = true;

            if (node.gobjects is not { Count: > 0 } nodeTriangles)
                continue;

            for (int triangleIndex = 0; triangleIndex < nodeTriangles.Count; triangleIndex++)
            {
                Triangle triangle = nodeTriangles[triangleIndex];
                if (!GeoUtil.Intersect.RayWithTriangle(start, direction, triangle.A, triangle.B, triangle.C, out float distance) ||
                    distance < 0.0f || distance > length)
                {
                    continue;
                }

                acceleratedHitCount++;
                acceleratedDistance = MathF.Min(acceleratedDistance, distance);
            }
        }

        float bruteForceDistance = float.PositiveInfinity;
        int bruteForceHitCount = 0;
        for (int i = 0; i < triangles.Count; i++)
        {
            Triangle triangle = triangles[i];
            if (!GeoUtil.Intersect.RayWithTriangle(start, direction, triangle.A, triangle.B, triangle.C, out float distance) ||
                distance < 0.0f || distance > length)
            {
                continue;
            }

            bruteForceHitCount++;
            bruteForceDistance = MathF.Min(bruteForceDistance, distance);
        }

        bool bothMiss = float.IsPositiveInfinity(acceleratedDistance) && float.IsPositiveInfinity(bruteForceDistance);
        bool sameClosestHit = bothMiss || MathF.Abs(acceleratedDistance - bruteForceDistance) <= 1e-4f;
        bool passed = sameClosestHit && acceleratedHitCount == bruteForceHitCount;
        _lastMeshHitPoint = float.IsPositiveInfinity(acceleratedDistance)
            ? null
            : start + direction * acceleratedDistance;

        Interlocked.Increment(ref _queryOperationCount);
        SetValidationState(ready: true, passed: passed, hitCount: acceleratedHitCount);
    }

    private bool CpuMeshNodeIntersectsQuery(AABB bounds)
        => GeoUtil.Intersect.SegmentWithAABB(
            _meshQuerySegment.Start,
            _meshQuerySegment.End,
            bounds.Min,
            bounds.Max,
            out _,
            out _);

    private void ReleaseCpuWorkload()
    {
        _cpuSceneTree = null;
        _cpuSceneItems = null;
        _sceneHitCollector = null;
        _sceneTreeDebugRenderer = null;
        _cpuMeshTree = null;
        _cpuMeshNodes = null;
        _cpuMeshVisitedNodes = null;
        _cpuMeshNodeTest = null;
    }

    private sealed class SceneBvhItem(AABB bounds) : IOctreeItem
    {
        public AABB BaseBounds { get; } = bounds;
        public bool QueryHit { get; set; }
        public bool ShouldRender => true;
        public IRenderableBase? Owner => null;
        public AABB? LocalCullingVolume { get; set; } = bounds;
        public Matrix4x4 CullingOffsetMatrix => Matrix4x4.Identity;
        public OctreeNodeBase? OctreeNode { get; set; }
    }
}

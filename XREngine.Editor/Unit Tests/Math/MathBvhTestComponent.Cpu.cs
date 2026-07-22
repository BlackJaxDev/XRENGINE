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
    private DelRenderBvhNodeAABB? _sceneTreeBaseDebugRenderer;
    private DelRenderBvhNodeAABB? _sceneTreeQueryDebugRenderer;
    private readonly MathBvhQueryVolume _queryVolume = new();
    private int _sceneQueryHitCount;
    private int _sceneMoveCursor;

    private BVH<Triangle>? _cpuMeshTree;
    private List<BVHNode<Triangle>>? _cpuMeshNodes;
    private bool[]? _cpuMeshVisitedNodes;
    private BVH<Triangle>.NodeTest? _cpuMeshNodeTest;
    private Dictionary<Triangle, int>? _cpuMeshTriangleIndices;
    private EContainment[]? _meshPointQueryContainments;
    private EContainment[]? _meshLineQueryContainments;
    private EContainment[]? _meshTriangleQueryContainments;
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
        _sceneTreeBaseDebugRenderer = RenderCpuSceneTreeBaseNode;
        _sceneTreeQueryDebugRenderer = RenderCpuSceneTreeQueryNode;
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
        _cpuMeshTriangleIndices = new Dictionary<Triangle, int>(triangles.Count);
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            _cpuMeshTriangleIndices.TryAdd(triangles[triangleIndex], triangleIndex);
        EnsureMeshQueryResultStorage();
        Interlocked.Increment(ref _buildOperationCount);
        ValidateCpuMeshQuery((float)Engine.ElapsedTime);
    }

    private void UpdateCpuSceneWorkload()
    {
        if (_cpuSceneTree is null || _cpuSceneItems is null)
            return;

        float time = (float)Engine.ElapsedTime;
        for (int move = 0; move < SceneMovesPerUpdate; move++)
        {
            int index = _sceneMoveCursor++ % _cpuSceneItems.Length;
            SceneBvhItem item = _cpuSceneItems[index];
            item.LocalCullingVolume = AnimateSceneBounds(item.BaseBounds, time, index);
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

        _queryVolume.Update(QueryShape, time);

        int bruteForceCount = 0;
        for (int i = 0; i < _cpuSceneItems.Length; i++)
        {
            SceneBvhItem item = _cpuSceneItems[i];
            item.QueryContainment = EContainment.Disjoint;
            if (item.LocalCullingVolume is { } itemBounds &&
                _queryVolume.ContainsAABB(itemBounds) != EContainment.Disjoint)
            {
                bruteForceCount++;
            }
        }

        _sceneQueryHitCount = 0;
        _cpuSceneTree.CollectVisible(
            _queryVolume,
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
        if (item.LocalCullingVolume is { } bounds)
            item.QueryContainment = _queryVolume.ContainsAABB(bounds);
        _sceneQueryHitCount++;
    }

    private static bool SceneItemIntersects(SceneBvhItem item, IVolume? volume, bool containsOnly)
    {
        if (item.LocalCullingVolume is not { } itemBounds || volume is null)
            return true;

        EContainment containment = volume.ContainsAABB(itemBounds);
        return containsOnly
            ? containment == EContainment.Contains
            : containment != EContainment.Disjoint;
    }

    private void UpdateCpuMeshWorkload()
    {
        if (_cpuMeshTree is null)
            return;

        ValidateCpuMeshQuery((float)Engine.ElapsedTime);
        Interlocked.Increment(ref _updateOperationCount);
    }

    private void ValidateCpuMeshQuery(float time)
    {
        if (_cpuMeshTree is null ||
            _cpuMeshVisitedNodes is null ||
            _cpuMeshNodeTest is null ||
            _cpuMeshTriangleIndices is null ||
            _sourceTriangles is not { Count: > 0 } triangles)
        {
            return;
        }

        _queryVolume.Update(QueryShape, time);
        _meshQuerySegment = _queryVolume.Raycast;
        EnsureMeshQueryResultStorage();
        ClearMeshQueryResultStorage();
        Array.Clear(_cpuMeshVisitedNodes);

        // The legacy API owns and returns this list. Retaining that allocation is intentional:
        // the benchmark measures the traversal contract that production callers still use.
        List<BVHNode<Triangle>> candidates = _cpuMeshTree.Traverse(_cpuMeshNodeTest);
        for (int nodeIndex = 0; nodeIndex < candidates.Count; nodeIndex++)
        {
            BVHNode<Triangle> node = candidates[nodeIndex];
            if ((uint)node.nodeNumber < (uint)_cpuMeshVisitedNodes.Length)
                _cpuMeshVisitedNodes[node.nodeNumber] = true;
        }

        if (QueryShape == MathBvhQueryShape.Raycast)
            ValidateCpuMeshRay(candidates, triangles);
        else
            ValidateCpuMeshShape(candidates, triangles);
    }

    private void ValidateCpuMeshRay(
        IReadOnlyList<BVHNode<Triangle>> candidates,
        IReadOnlyList<Triangle> triangles)
    {
        Vector3 start = _meshQuerySegment.Start;
        Vector3 end = _meshQuerySegment.End;
        float acceleratedDistance = float.PositiveInfinity;
        int acceleratedHitCount = 0;
        Vector3 direction = end - start;
        float length = direction.Length();
        if (length > 1e-6f)
            direction /= length;

        for (int nodeIndex = 0; nodeIndex < candidates.Count; nodeIndex++)
        {
            BVHNode<Triangle> node = candidates[nodeIndex];
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
                if (_cpuMeshTriangleIndices?.TryGetValue(triangle, out int sourceIndex) == true &&
                    _meshTriangleQueryContainments is not null)
                {
                    _meshTriangleQueryContainments[sourceIndex] = EContainment.Intersects;
                }
            }
        }

        CalculateMeshBruteForce(triangles, _meshQuerySegment, out int bruteForceHitCount, out float bruteForceDistance);

        bool bothMiss = float.IsPositiveInfinity(acceleratedDistance) && float.IsPositiveInfinity(bruteForceDistance);
        bool sameClosestHit = bothMiss || MathF.Abs(acceleratedDistance - bruteForceDistance) <= 1e-4f;
        bool passed = sameClosestHit && acceleratedHitCount == bruteForceHitCount;
        _lastMeshHitPoint = float.IsPositiveInfinity(acceleratedDistance)
            ? null
            : start + direction * acceleratedDistance;

        Interlocked.Increment(ref _queryOperationCount);
        SetExpectedHitCounts(0, 0, bruteForceHitCount);
        SetValidationState(
            ready: true,
            passed: passed,
            hitCount: acceleratedHitCount,
            triangleHitCount: acceleratedHitCount);
    }

    private void ValidateCpuMeshShape(
        IReadOnlyList<BVHNode<Triangle>> candidates,
        IReadOnlyList<Triangle> triangles)
    {
        int acceleratedPointCount = 0;
        int acceleratedLineCount = 0;
        int acceleratedTriangleCount = 0;
        for (int nodeIndex = 0; nodeIndex < candidates.Count; nodeIndex++)
        {
            if (candidates[nodeIndex].gobjects is not { Count: > 0 } nodeTriangles)
                continue;

            for (int triangleIndex = 0; triangleIndex < nodeTriangles.Count; triangleIndex++)
            {
                Triangle triangle = nodeTriangles[triangleIndex];
                if (_cpuMeshTriangleIndices?.TryGetValue(triangle, out int sourceIndex) != true)
                    continue;

                AccumulateMeshShapeHits(
                    triangle,
                    sourceIndex,
                    writeResults: true,
                    ref acceleratedPointCount,
                    ref acceleratedLineCount,
                    ref acceleratedTriangleCount);
            }
        }

        int expectedPointCount = 0;
        int expectedLineCount = 0;
        int expectedTriangleCount = 0;
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            AccumulateMeshShapeHits(
                triangles[triangleIndex],
                triangleIndex,
                writeResults: false,
                ref expectedPointCount,
                ref expectedLineCount,
                ref expectedTriangleCount);
        }

        int totalHitCount = acceleratedPointCount + acceleratedLineCount + acceleratedTriangleCount;
        bool passed =
            acceleratedPointCount == expectedPointCount &&
            acceleratedLineCount == expectedLineCount &&
            acceleratedTriangleCount == expectedTriangleCount;
        _lastMeshHitPoint = null;
        Interlocked.Increment(ref _queryOperationCount);
        SetExpectedHitCounts(expectedPointCount, expectedLineCount, expectedTriangleCount);
        SetValidationState(
            ready: true,
            passed,
            totalHitCount,
            acceleratedPointCount,
            acceleratedLineCount,
            acceleratedTriangleCount);
    }

    private void AccumulateMeshShapeHits(
        in Triangle triangle,
        int triangleIndex,
        bool writeResults,
        ref int pointCount,
        ref int lineCount,
        ref int triangleCount)
    {
        int elementOffset = triangleIndex * 3;
        if (QueryPoints)
        {
            AccumulateMeshPointHit(triangle.A, elementOffset, writeResults, ref pointCount);
            AccumulateMeshPointHit(triangle.B, elementOffset + 1, writeResults, ref pointCount);
            AccumulateMeshPointHit(triangle.C, elementOffset + 2, writeResults, ref pointCount);
        }

        if (QueryLines)
        {
            AccumulateMeshLineHit(new Segment(triangle.A, triangle.B), elementOffset, writeResults, ref lineCount);
            AccumulateMeshLineHit(new Segment(triangle.B, triangle.C), elementOffset + 1, writeResults, ref lineCount);
            AccumulateMeshLineHit(new Segment(triangle.C, triangle.A), elementOffset + 2, writeResults, ref lineCount);
        }

        if (QueryTriangles)
        {
            EContainment containment = _queryVolume.ClassifyTriangle(triangle);
            if (containment != EContainment.Disjoint)
                triangleCount++;
            if (writeResults && _meshTriangleQueryContainments is not null)
                _meshTriangleQueryContainments[triangleIndex] = containment;
        }
    }

    private void AccumulateMeshPointHit(Vector3 point, int resultIndex, bool writeResult, ref int pointCount)
    {
        EContainment containment = _queryVolume.ClassifyPoint(point);
        if (containment != EContainment.Disjoint)
            pointCount++;
        if (writeResult && _meshPointQueryContainments is not null)
            _meshPointQueryContainments[resultIndex] = containment;
    }

    private void AccumulateMeshLineHit(in Segment line, int resultIndex, bool writeResult, ref int lineCount)
    {
        EContainment containment = _queryVolume.ClassifySegment(line);
        if (containment != EContainment.Disjoint)
            lineCount++;
        if (writeResult && _meshLineQueryContainments is not null)
            _meshLineQueryContainments[resultIndex] = containment;
    }

    private bool CpuMeshNodeIntersectsQuery(AABB bounds)
        => _queryVolume.ContainsAABB(bounds) != EContainment.Disjoint;

    private void EnsureMeshQueryResultStorage()
    {
        int triangleCount = _sourceTriangles?.Count ?? 0;
        int elementCount = triangleCount * 3;
        if (_meshPointQueryContainments?.Length != elementCount)
            _meshPointQueryContainments = new EContainment[elementCount];
        if (_meshLineQueryContainments?.Length != elementCount)
            _meshLineQueryContainments = new EContainment[elementCount];
        if (_meshTriangleQueryContainments?.Length != triangleCount)
            _meshTriangleQueryContainments = new EContainment[triangleCount];
    }

    private void ClearMeshQueryResultStorage()
    {
        if (_meshPointQueryContainments is not null)
            Array.Fill(_meshPointQueryContainments, EContainment.Disjoint);
        if (_meshLineQueryContainments is not null)
            Array.Fill(_meshLineQueryContainments, EContainment.Disjoint);
        if (_meshTriangleQueryContainments is not null)
            Array.Fill(_meshTriangleQueryContainments, EContainment.Disjoint);
    }

    private void ReleaseCpuWorkload()
    {
        _cpuSceneTree = null;
        _cpuSceneItems = null;
        _sceneHitCollector = null;
        _sceneTreeBaseDebugRenderer = null;
        _sceneTreeQueryDebugRenderer = null;
        _cpuMeshTree = null;
        _cpuMeshNodes = null;
        _cpuMeshVisitedNodes = null;
        _cpuMeshNodeTest = null;
        _cpuMeshTriangleIndices = null;
        _meshPointQueryContainments = null;
        _meshLineQueryContainments = null;
        _meshTriangleQueryContainments = null;
    }

    private sealed class SceneBvhItem(AABB bounds) : IOctreeItem
    {
        public AABB BaseBounds { get; } = bounds;
        public EContainment QueryContainment { get; set; } = EContainment.Disjoint;
        public bool ShouldRender => true;
        public IRenderableBase? Owner => null;
        public AABB? LocalCullingVolume { get; set; } = bounds;
        public Matrix4x4 CullingOffsetMatrix => Matrix4x4.Identity;
        public OctreeNodeBase? OctreeNode { get; set; }
    }
}

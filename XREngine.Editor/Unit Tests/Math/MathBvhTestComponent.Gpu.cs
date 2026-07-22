using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Editor;

public sealed partial class MathBvhTestComponent
{
    private const int GpuQueryHistoryCapacity = 32;
    private const uint GpuQueryResultWordCount = 12u;

    private GpuBvhTree? _gpuSceneTree;
    private XRDataBuffer? _gpuSceneAabbBuffer;
    private GpuSceneAabb[]? _gpuSceneAabbs;
    private AABB[]? _gpuSceneBaseBounds;
    private AABB[]? _gpuSceneCurrentBounds;
    private EContainment[]? _gpuSceneQueryContainments;
    private XRDataBuffer? _gpuSceneNodeClassesBuffer;
    private XRDataBuffer? _gpuSceneQueryResultBuffer;
    private XRDataBuffer? _gpuScenePrimitiveClassesBuffer;
    private XRShader? _gpuSceneQueryShader;
    private XRRenderProgram? _gpuSceneQueryProgram;
    private readonly uint[] _gpuSceneExpectedQueryIds = new uint[GpuQueryHistoryCapacity];
    private readonly int[] _gpuSceneExpectedHitCounts = new int[GpuQueryHistoryCapacity];
    private uint _gpuSceneLastCompletedQueryId;
    private bool _gpuSceneQueryEverReady;
    private int _gpuSceneLastExpectedHitCount;
    private uint _gpuSceneLastTraversalFlags;
    private uint _gpuSceneLastHeaderNodeCount;
    private uint _gpuSceneLastHeaderRootIndex;
    private uint _gpuSceneLastAabbCount;
    private uint _gpuSceneLastMortonScalarCount;
    private uint _gpuSceneLastRecoveryQueryId;
    private uint[]? _gpuScenePrimitiveClassReadback;
    private int _gpuSceneMoveCursor;

    private RenderableMesh? _gpuMesh;
    private bool _gpuMeshEverReady;
    private XRDataBuffer? _gpuMeshNodeClassesBuffer;
    private XRDataBuffer? _gpuMeshQueryResultBuffer;
    private XRDataBuffer? _gpuMeshPrimitiveClassesBuffer;
    private XRShader? _gpuMeshQueryShader;
    private XRRenderProgram? _gpuMeshQueryProgram;
    private readonly uint[] _gpuMeshExpectedQueryIds = new uint[GpuQueryHistoryCapacity];
    private readonly int[] _gpuMeshExpectedHitCounts = new int[GpuQueryHistoryCapacity];
    private readonly float[] _gpuMeshExpectedClosestDistances = new float[GpuQueryHistoryCapacity];
    private readonly int[] _gpuMeshExpectedPointHitCounts = new int[GpuQueryHistoryCapacity];
    private readonly int[] _gpuMeshExpectedLineHitCounts = new int[GpuQueryHistoryCapacity];
    private readonly int[] _gpuMeshExpectedTriangleHitCounts = new int[GpuQueryHistoryCapacity];
    private readonly uint[] _gpuMeshExpectedPrimitiveMasks = new uint[GpuQueryHistoryCapacity];
    private uint[]? _gpuMeshPrimitiveClassReadback;
    private uint _gpuMeshLastCompletedQueryId;
    private bool _gpuMeshQueryEverReady;
    private int _gpuMeshLastExpectedHitCount;
    private float _gpuMeshLastExpectedClosestDistance;
    private float _gpuMeshLastClosestDistance;
    private uint _gpuMeshLastTraversalFlags;
    private uint _gpuMeshLastHeaderNodeCount;
    private uint _gpuMeshLastHeaderRootIndex;
    private uint _gpuMeshLastTriangleCount;
    private uint _gpuMeshLastRecoveryQueryId;
    private uint _nextGpuQueryId;

    private void PrepareGpuSceneWorkload()
    {
        EnsureGpuSceneResources();
        if (_gpuSceneTree is null ||
            _gpuSceneAabbBuffer is null ||
            _gpuSceneAabbs is null ||
            _gpuSceneBaseBounds is null ||
            _gpuSceneCurrentBounds is null)
        {
            SetValidationState(ready: false, passed: false);
            return;
        }

        float time = (float)Engine.ElapsedTime;
        uint stride = (uint)Marshal.SizeOf<GpuSceneAabb>();
        for (int move = 0; move < SceneMovesPerUpdate; move++)
        {
            int movedIndex = _gpuSceneMoveCursor++ % _gpuSceneAabbs.Length;
            AABB movedBounds = AnimateSceneBounds(_gpuSceneBaseBounds[movedIndex], time, movedIndex);
            _gpuSceneCurrentBounds[movedIndex] = movedBounds;
            GpuSceneAabb gpuBounds = GpuSceneAabb.FromBounds(movedBounds);
            _gpuSceneAabbs[movedIndex] = gpuBounds;
            _gpuSceneAabbBuffer.SetDataRawAtIndex((uint)movedIndex, gpuBounds);
            _gpuSceneAabbBuffer.PushSubData(checked((int)((uint)movedIndex * stride)), stride);
        }

        if (_gpuSceneTree.IsDirty || _gpuSceneTree.NodeCount == 0u)
            _gpuSceneTree.Build(_gpuSceneAabbBuffer, (uint)_gpuSceneAabbs.Length, s_sceneTreeBounds);
        else
            _gpuSceneTree.Refit();

        GpuBvhDiagnostics diagnostics = _gpuSceneTree.Diagnostics;
        Interlocked.Exchange(ref _buildOperationCount, checked((long)diagnostics.BuildCount));
        Interlocked.Exchange(ref _updateOperationCount, checked((long)diagnostics.RefitCount));

        uint expectedNodes = CalculateBinaryBvhNodeCount((uint)_gpuSceneAabbs.Length, SceneLeafCapacity);
        bool topologyReady =
            !_gpuSceneTree.IsDirty &&
            _gpuSceneTree.NodeBuffer is not null &&
            _gpuSceneTree.MortonBuffer is not null &&
            _gpuSceneTree.PrimitiveCount == (uint)_gpuSceneAabbs.Length &&
            _gpuSceneTree.NodeCount == expectedNodes;
        if (!topologyReady)
        {
            SetValidationState(ready: false, passed: false);
            return;
        }

        PrepareSceneQueryOracle(time);
        uint queryId = NextGpuQueryId();
        int historyIndex = GetGpuQueryHistoryIndex(queryId);
        _gpuSceneExpectedQueryIds[historyIndex] = queryId;
        _gpuSceneExpectedHitCounts[historyIndex] = CountGpuSceneOracleHits();

        if (!DispatchGpuSceneQuery(queryId))
        {
            if (!_gpuSceneQueryEverReady)
                SetValidationState(ready: false, passed: false);
            return;
        }

        Interlocked.Increment(ref _queryOperationCount);
        if (!TryConsumeGpuSceneQueryResult() && !_gpuSceneQueryEverReady)
            SetValidationState(ready: false, passed: false);
    }

    private void EnsureGpuSceneResources()
    {
        if (_gpuSceneTree is not null && _gpuSceneAabbBuffer is not null)
            return;

        _gpuSceneBaseBounds = CreateScenePrimitiveBounds();
        _gpuSceneCurrentBounds = (AABB[])_gpuSceneBaseBounds.Clone();
        _gpuSceneQueryContainments = new EContainment[_gpuSceneBaseBounds.Length];
        Array.Fill(_gpuSceneQueryContainments, EContainment.Disjoint);
        _gpuSceneAabbs = new GpuSceneAabb[_gpuSceneBaseBounds.Length];
        for (int i = 0; i < _gpuSceneBaseBounds.Length; i++)
            _gpuSceneAabbs[i] = GpuSceneAabb.FromBounds(_gpuSceneBaseBounds[i]);

        _gpuSceneAabbBuffer = new XRDataBuffer(
            "MathBvhTest.SceneAabbs",
            EBufferTarget.ShaderStorageBuffer,
            (uint)_gpuSceneAabbs.Length,
            EComponentType.Struct,
            (uint)Marshal.SizeOf<GpuSceneAabb>(),
            false,
            true)
        {
            Usage = EBufferUsage.DynamicDraw,
            Resizable = true,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
            ShouldMap = false,
        };
        _gpuSceneAabbBuffer.SetDataRaw(_gpuSceneAabbs.AsSpan());
        _gpuSceneAabbBuffer.PushData();

        _gpuSceneTree = new GpuBvhTree("MathIntersections.SceneBvh")
        {
            BuildMode = BvhBuildMode.MortonOnly,
            MaxLeafPrimitives = SceneLeafCapacity,
        };
    }

    private void PrepareGpuMeshWorkload()
    {
        _gpuMesh ??= ResolveGpuMesh();
        if (_gpuMesh is null)
        {
            SetValidationState(ready: false, passed: false);
            return;
        }

        bool prepared = _gpuMesh.PrepareGpuMeshBvh(
            realtimeSkinned: false,
            forceRebuild: false,
            maxLeafPrimitives: GpuMeshBvh.DefaultMaxLeafPrimitives);
        Interlocked.Increment(ref _updateOperationCount);

        GpuMeshBvh? bvh = _gpuMesh.GpuMeshBvh;
        bool ready = prepared && bvh?.IsBvhReady == true;
        if (!ready)
        {
            // Shader compilation, buffer uploads, and source-revision refreshes can make Prepare
            // temporarily pending. Preserve the last completed validation instead of reporting a
            // false failure between two valid GPU states.
            if (!_gpuMeshEverReady)
                SetValidationState(ready: false, passed: false);
            return;
        }

        if (!_gpuMeshEverReady)
        {
            _gpuMeshEverReady = true;
            Interlocked.Increment(ref _buildOperationCount);
        }

        uint triangleCount = bvh!.TriangleCount;
        uint expectedNodes = CalculateBinaryBvhNodeCount(triangleCount, GpuMeshBvh.DefaultMaxLeafPrimitives);
        bool topologyValid =
            triangleCount == (uint)(_sourceTriangles?.Count ?? 0) &&
            bvh.BvhNodeCount == expectedNodes &&
            bvh.BvhNodeBuffer is not null &&
            bvh.PackedTriangleBuffer is not null;
        if (!topologyValid || _sourceTriangles is not { Count: > 0 } triangles)
        {
            SetValidationState(ready: true, passed: false);
            return;
        }

        float time = (float)Engine.ElapsedTime;
        _queryVolume.Update(QueryShape, time);
        _meshQuerySegment = _queryVolume.Raycast;
        EnsureMeshQueryResultStorage();
        ClearMeshQueryResultStorage();

        int expectedHitCount;
        int expectedPointHitCount = 0;
        int expectedLineHitCount = 0;
        int expectedTriangleHitCount;
        float expectedClosestDistance;
        if (QueryShape == MathBvhQueryShape.Raycast)
        {
            CalculateMeshBruteForce(triangles, _meshQuerySegment, out expectedHitCount, out expectedClosestDistance);
            expectedTriangleHitCount = expectedHitCount;
            Vector3 queryDirection = _meshQuerySegment.End - _meshQuerySegment.Start;
            float queryLength = queryDirection.Length();
            if (queryLength > 1e-6f)
                queryDirection /= queryLength;
            _lastMeshHitPoint = float.IsPositiveInfinity(expectedClosestDistance)
                ? null
                : _meshQuerySegment.Start + queryDirection * expectedClosestDistance;
        }
        else
        {
            expectedTriangleHitCount = 0;
            for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            {
                AccumulateMeshShapeHits(
                    triangles[triangleIndex],
                    triangleIndex,
                    writeResults: false,
                    ref expectedPointHitCount,
                    ref expectedLineHitCount,
                    ref expectedTriangleHitCount);
            }

            expectedHitCount = expectedPointHitCount + expectedLineHitCount + expectedTriangleHitCount;
            expectedClosestDistance = float.PositiveInfinity;
            _lastMeshHitPoint = null;
        }

        uint queryId = NextGpuQueryId();
        int historyIndex = GetGpuQueryHistoryIndex(queryId);
        _gpuMeshExpectedQueryIds[historyIndex] = queryId;
        _gpuMeshExpectedHitCounts[historyIndex] = expectedHitCount;
        _gpuMeshExpectedClosestDistances[historyIndex] = expectedClosestDistance;
        _gpuMeshExpectedPointHitCounts[historyIndex] = expectedPointHitCount;
        _gpuMeshExpectedLineHitCounts[historyIndex] = expectedLineHitCount;
        _gpuMeshExpectedTriangleHitCounts[historyIndex] = expectedTriangleHitCount;
        _gpuMeshExpectedPrimitiveMasks[historyIndex] = GetMeshQueryPrimitiveMask();

        if (!DispatchGpuMeshQuery(bvh, queryId))
        {
            if (!_gpuMeshQueryEverReady)
                SetValidationState(ready: false, passed: false);
            return;
        }

        Interlocked.Increment(ref _queryOperationCount);
        if (!TryConsumeGpuMeshQueryResult() && !_gpuMeshQueryEverReady)
            SetValidationState(ready: false, passed: false);
    }

    private void PrepareSceneQueryOracle(float time)
    {
        _queryVolume.Update(QueryShape, time);
    }

    private int CountGpuSceneOracleHits()
    {
        if (_gpuSceneCurrentBounds is null)
            return 0;

        int count = 0;
        for (int i = 0; i < _gpuSceneCurrentBounds.Length; i++)
            if (_queryVolume.ContainsAABB(_gpuSceneCurrentBounds[i]) != EContainment.Disjoint)
                count++;
        return count;
    }

    private bool DispatchGpuSceneQuery(uint queryId)
    {
        if (_gpuSceneTree?.NodeBuffer is not { } nodeBuffer ||
            _gpuSceneTree.MortonBuffer is not { } mortonBuffer ||
            _gpuSceneAabbBuffer is null)
        {
            return false;
        }

        EnsureGpuUIntBuffer(
            ref _gpuSceneNodeClassesBuffer,
            "MathBvhTest.SceneNodeClasses",
            _gpuSceneTree.NodeCount,
            readback: false);
        EnsureGpuUIntBuffer(
            ref _gpuSceneQueryResultBuffer,
            "MathBvhTest.SceneQueryResult",
            GpuQueryResultWordCount,
            readback: true);
        EnsureGpuUIntBuffer(
            ref _gpuScenePrimitiveClassesBuffer,
            "MathBvhTest.ScenePrimitiveClasses",
            (uint)(_gpuSceneAabbs?.Length ?? 0),
            readback: true);
        if (_gpuSceneNodeClassesBuffer is null ||
            _gpuSceneQueryResultBuffer is null ||
            _gpuScenePrimitiveClassesBuffer is null ||
            !EnsureGpuQueryProgram(
                ref _gpuSceneQueryShader,
                ref _gpuSceneQueryProgram,
                "Scene3D/RenderPipeline/math_bvh_scene_query.comp",
                "MathBvhSceneQuery"))
        {
            return false;
        }

        XRRenderProgram program = _gpuSceneQueryProgram!;
        program.BindBuffer(nodeBuffer, 0);
        program.BindBuffer(mortonBuffer, 1);
        program.BindBuffer(_gpuSceneAabbBuffer, 2);
        program.BindBuffer(_gpuSceneNodeClassesBuffer, 3);
        program.BindBuffer(_gpuSceneQueryResultBuffer, 4);
        program.BindBuffer(_gpuScenePrimitiveClassesBuffer, 5);
        BindGpuQueryUniforms(program);
        program.Uniform("LogicalNodeCount", _gpuSceneTree.NodeCount);
        program.Uniform("QueryId", queryId);
        program.DispatchCompute(
            1u,
            1u,
            1u,
            EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.ClientMappedBuffer);
        return true;
    }

    private void BindGpuQueryUniforms(XRRenderProgram program)
    {
        AABB box = _queryVolume.Box;
        Sphere sphere = _queryVolume.Sphere;
        Frustum frustum = _queryVolume.Frustum;
        program.Uniform("QueryShape", (uint)_queryVolume.Shape);
        program.Uniform("QueryMin", box.Min);
        program.Uniform("QueryMax", box.Max);
        program.Uniform("QuerySphereCenter", sphere.Center);
        program.Uniform("QuerySphereRadius", sphere.Radius);
        program.Uniform("QueryRayStart", _queryVolume.Raycast.Start);
        program.Uniform("QueryRayEnd", _queryVolume.Raycast.End);
        program.Uniform("QueryFrustumPlane0", ToPlaneVector(frustum.Left));
        program.Uniform("QueryFrustumPlane1", ToPlaneVector(frustum.Right));
        program.Uniform("QueryFrustumPlane2", ToPlaneVector(frustum.Bottom));
        program.Uniform("QueryFrustumPlane3", ToPlaneVector(frustum.Top));
        program.Uniform("QueryFrustumPlane4", ToPlaneVector(frustum.Near));
        program.Uniform("QueryFrustumPlane5", ToPlaneVector(frustum.Far));
        program.Uniform("QueryFrustumCorner0", frustum.LeftBottomNear);
        program.Uniform("QueryFrustumCorner1", frustum.RightBottomNear);
        program.Uniform("QueryFrustumCorner2", frustum.LeftTopNear);
        program.Uniform("QueryFrustumCorner3", frustum.RightTopNear);
        program.Uniform("QueryFrustumCorner4", frustum.LeftBottomFar);
        program.Uniform("QueryFrustumCorner5", frustum.RightBottomFar);
        program.Uniform("QueryFrustumCorner6", frustum.LeftTopFar);
        program.Uniform("QueryFrustumCorner7", frustum.RightTopFar);
    }

    private static Vector4 ToPlaneVector(Plane plane)
        => new(plane.Normal, plane.D);

    private bool DispatchGpuMeshQuery(GpuMeshBvh bvh, uint queryId)
    {
        if (bvh.BvhNodeBuffer is not { } nodeBuffer || bvh.PackedTriangleBuffer is not { } triangleBuffer)
            return false;

        EnsureGpuUIntBuffer(
            ref _gpuMeshNodeClassesBuffer,
            "MathBvhTest.MeshNodeClasses",
            bvh.BvhNodeCount,
            readback: false);
        EnsureGpuUIntBuffer(
            ref _gpuMeshQueryResultBuffer,
            "MathBvhTest.MeshQueryResult",
            GpuQueryResultWordCount,
            readback: true);
        EnsureGpuUIntBuffer(
            ref _gpuMeshPrimitiveClassesBuffer,
            "MathBvhTest.MeshPrimitiveClasses",
            bvh.TriangleCount,
            readback: true);
        if (_gpuMeshNodeClassesBuffer is null ||
            _gpuMeshQueryResultBuffer is null ||
            _gpuMeshPrimitiveClassesBuffer is null ||
            !EnsureGpuQueryProgram(
                ref _gpuMeshQueryShader,
                ref _gpuMeshQueryProgram,
                "Scene3D/RenderPipeline/math_bvh_mesh_query.comp",
                "MathBvhMeshQuery"))
        {
            return false;
        }

        XRRenderProgram program = _gpuMeshQueryProgram!;
        program.BindBuffer(nodeBuffer, 0);
        program.BindBuffer(triangleBuffer, 1);
        program.BindBuffer(_gpuMeshNodeClassesBuffer, 2);
        program.BindBuffer(_gpuMeshQueryResultBuffer, 3);
        program.BindBuffer(_gpuMeshPrimitiveClassesBuffer, 4);
        BindGpuQueryUniforms(program);
        program.Uniform("QueryPrimitiveMask", GetMeshQueryPrimitiveMask());
        program.Uniform("LogicalNodeCount", bvh.BvhNodeCount);
        program.Uniform("QueryId", queryId);
        program.DispatchCompute(
            1u,
            1u,
            1u,
            EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.ClientMappedBuffer);
        return true;
    }

    private bool TryConsumeGpuSceneQueryResult()
    {
        if (_gpuSceneQueryResultBuffer is null)
            return false;

        Span<uint> result = stackalloc uint[(int)GpuQueryResultWordCount];
        if (!TryReadGpuUIntBuffer(_gpuSceneQueryResultBuffer, result))
            return false;

        uint queryId = result[0];
        if (queryId == 0u || queryId == _gpuSceneLastCompletedQueryId)
            return false;

        _gpuSceneLastCompletedQueryId = queryId;
        int historyIndex = GetGpuQueryHistoryIndex(queryId);
        bool knownQuery = _gpuSceneExpectedQueryIds[historyIndex] == queryId;
        int hitCount = checked((int)result[1]);
        _gpuSceneLastExpectedHitCount = knownQuery ? _gpuSceneExpectedHitCounts[historyIndex] : -1;
        _gpuSceneLastTraversalFlags = result[3];
        _gpuSceneLastHeaderNodeCount = result[4];
        _gpuSceneLastHeaderRootIndex = result[5];
        _gpuSceneLastAabbCount = result[6];
        _gpuSceneLastMortonScalarCount = result[7];
        uint expectedPrimitiveCount = (uint)(_gpuSceneAabbs?.Length ?? 0);
        uint expectedNodeCount = _gpuSceneTree?.NodeCount ?? 0u;
        bool headerValid =
            result[4] == expectedNodeCount &&
            result[5] < expectedNodeCount &&
            result[6] == expectedPrimitiveCount &&
            result[7] >= expectedPrimitiveCount * 2u;
        bool primitiveResultsReady = TryConsumeGpuScenePrimitiveClasses(
            expectedPrimitiveCount,
            out int bufferedHitCount);
        bool passed =
            knownQuery &&
            headerValid &&
            result[3] == 0u &&
            hitCount == _gpuSceneExpectedHitCounts[historyIndex] &&
            primitiveResultsReady &&
            bufferedHitCount == hitCount;
        if (!headerValid &&
            (_gpuSceneLastRecoveryQueryId == 0u || queryId - _gpuSceneLastRecoveryQueryId >= GpuQueryHistoryCapacity))
        {
            _gpuSceneLastRecoveryQueryId = queryId;
            _gpuSceneTree?.MarkDirty();
        }
        _gpuSceneQueryEverReady = true;
        SetValidationState(ready: true, passed, hitCount);
        return true;
    }

    private bool TryConsumeGpuMeshQueryResult()
    {
        if (_gpuMeshQueryResultBuffer is null)
            return false;

        Span<uint> result = stackalloc uint[(int)GpuQueryResultWordCount];
        if (!TryReadGpuUIntBuffer(_gpuMeshQueryResultBuffer, result))
            return false;

        uint queryId = result[0];
        if (queryId == 0u || queryId == _gpuMeshLastCompletedQueryId)
            return false;

        _gpuMeshLastCompletedQueryId = queryId;
        int historyIndex = GetGpuQueryHistoryIndex(queryId);
        bool knownQuery = _gpuMeshExpectedQueryIds[historyIndex] == queryId;
        int hitCount = checked((int)result[1]);
        float closestDistance = BitConverter.UInt32BitsToSingle(result[2]);
        float expectedDistance = _gpuMeshExpectedClosestDistances[historyIndex];
        _gpuMeshLastExpectedHitCount = knownQuery ? _gpuMeshExpectedHitCounts[historyIndex] : -1;
        _gpuMeshLastExpectedClosestDistance = knownQuery ? expectedDistance : float.NaN;
        _gpuMeshLastClosestDistance = closestDistance;
        _gpuMeshLastTraversalFlags = result[4];
        _gpuMeshLastHeaderNodeCount = result[5];
        _gpuMeshLastHeaderRootIndex = result[6];
        _gpuMeshLastTriangleCount = result[7];
        int pointHitCount = checked((int)result[8]);
        int lineHitCount = checked((int)result[9]);
        int triangleHitCount = checked((int)result[10]);
        uint primitiveMask = result[11];
        GpuMeshBvh? bvh = _gpuMesh?.GpuMeshBvh;
        uint expectedNodeCount = bvh?.BvhNodeCount ?? 0u;
        uint expectedTriangleCount = bvh?.TriangleCount ?? 0u;
        bool headerValid =
            result[5] == expectedNodeCount &&
            result[6] < expectedNodeCount &&
            result[7] == expectedTriangleCount;
        bool bothMiss = float.IsPositiveInfinity(closestDistance) && float.IsPositiveInfinity(expectedDistance);
        bool sameClosestHit = bothMiss || MathF.Abs(closestDistance - expectedDistance) <= 1e-4f;
        bool primitiveResultsReady = TryConsumeGpuMeshPrimitiveClasses(
            expectedTriangleCount,
            out int bufferedPointHitCount,
            out int bufferedLineHitCount,
            out int bufferedTriangleHitCount);
        bool passed =
            knownQuery &&
            headerValid &&
            result[4] == 0u &&
            hitCount == _gpuMeshExpectedHitCounts[historyIndex] &&
            pointHitCount == _gpuMeshExpectedPointHitCounts[historyIndex] &&
            lineHitCount == _gpuMeshExpectedLineHitCounts[historyIndex] &&
            triangleHitCount == _gpuMeshExpectedTriangleHitCounts[historyIndex] &&
            primitiveMask == _gpuMeshExpectedPrimitiveMasks[historyIndex] &&
            primitiveResultsReady &&
            bufferedPointHitCount == pointHitCount &&
            bufferedLineHitCount == lineHitCount &&
            bufferedTriangleHitCount == triangleHitCount &&
            sameClosestHit;
        if (!headerValid &&
            (_gpuMeshLastRecoveryQueryId == 0u || queryId - _gpuMeshLastRecoveryQueryId >= GpuQueryHistoryCapacity))
        {
            _gpuMeshLastRecoveryQueryId = queryId;
            bvh?.MarkDirty();
        }
        _gpuMeshQueryEverReady = true;
        SetExpectedHitCounts(
            knownQuery ? _gpuMeshExpectedPointHitCounts[historyIndex] : -1,
            knownQuery ? _gpuMeshExpectedLineHitCounts[historyIndex] : -1,
            knownQuery ? _gpuMeshExpectedTriangleHitCounts[historyIndex] : -1);
        SetValidationState(
            ready: true,
            passed,
            hitCount,
            pointHitCount,
            lineHitCount,
            triangleHitCount);
        return true;
    }

    private bool TryConsumeGpuScenePrimitiveClasses(uint primitiveCount, out int hitCount)
    {
        hitCount = 0;
        if (_gpuScenePrimitiveClassesBuffer is null)
            return false;

        int requiredCount = checked((int)primitiveCount);
        if (_gpuScenePrimitiveClassReadback?.Length != requiredCount)
            _gpuScenePrimitiveClassReadback = new uint[requiredCount];
        if (!TryReadGpuUIntBuffer(_gpuScenePrimitiveClassesBuffer, _gpuScenePrimitiveClassReadback))
            return false;

        if (_gpuSceneQueryContainments?.Length != requiredCount)
            _gpuSceneQueryContainments = new EContainment[requiredCount];
        for (int primitiveIndex = 0; primitiveIndex < requiredCount; primitiveIndex++)
        {
            if (!TryDecodeGpuContainment(_gpuScenePrimitiveClassReadback[primitiveIndex], out EContainment containment))
                return false;

            _gpuSceneQueryContainments[primitiveIndex] = containment;
            if (containment != EContainment.Disjoint)
                hitCount++;
        }

        return true;
    }

    private bool TryConsumeGpuMeshPrimitiveClasses(
        uint triangleCount,
        out int pointHitCount,
        out int lineHitCount,
        out int triangleHitCount)
    {
        pointHitCount = 0;
        lineHitCount = 0;
        triangleHitCount = 0;
        if (_gpuMeshPrimitiveClassesBuffer is null)
            return false;

        int requiredCount = checked((int)triangleCount);
        if (_gpuMeshPrimitiveClassReadback?.Length != requiredCount)
            _gpuMeshPrimitiveClassReadback = new uint[requiredCount];
        if (!TryReadGpuUIntBuffer(_gpuMeshPrimitiveClassesBuffer, _gpuMeshPrimitiveClassReadback))
            return false;

        EnsureMeshQueryResultStorage();
        ClearMeshQueryResultStorage();
        if (_meshPointQueryContainments is null ||
            _meshLineQueryContainments is null ||
            _meshTriangleQueryContainments is null)
        {
            return false;
        }

        for (int triangleIndex = 0; triangleIndex < requiredCount; triangleIndex++)
        {
            uint classes = _gpuMeshPrimitiveClassReadback[triangleIndex];
            int elementOffset = triangleIndex * 3;
            for (int pointIndex = 0; pointIndex < 3; pointIndex++)
            {
                if (!TryDecodeGpuContainment(
                    (classes >> (pointIndex * 2)) & 0b11u,
                    out EContainment containment))
                {
                    return false;
                }

                _meshPointQueryContainments[elementOffset + pointIndex] = containment;
                if (containment != EContainment.Disjoint)
                    pointHitCount++;
            }

            for (int lineIndex = 0; lineIndex < 3; lineIndex++)
            {
                if (!TryDecodeGpuContainment(
                    (classes >> ((lineIndex + 3) * 2)) & 0b11u,
                    out EContainment containment))
                {
                    return false;
                }

                _meshLineQueryContainments[elementOffset + lineIndex] = containment;
                if (containment != EContainment.Disjoint)
                    lineHitCount++;
            }

            if (!TryDecodeGpuContainment((classes >> 12) & 0b11u, out EContainment triangleContainment))
                return false;
            _meshTriangleQueryContainments[triangleIndex] = triangleContainment;
            if (triangleContainment != EContainment.Disjoint)
                triangleHitCount++;
        }

        return true;
    }

    private static bool TryDecodeGpuContainment(uint value, out EContainment containment)
    {
        containment = value switch
        {
            0u => EContainment.Disjoint,
            1u => EContainment.Contains,
            2u => EContainment.Intersects,
            _ => EContainment.Disjoint,
        };
        return value <= 2u;
    }

    private uint NextGpuQueryId()
    {
        _nextGpuQueryId++;
        if (_nextGpuQueryId == 0u)
            _nextGpuQueryId++;
        return _nextGpuQueryId;
    }

    private static int GetGpuQueryHistoryIndex(uint queryId)
        => (int)(queryId % GpuQueryHistoryCapacity);

    private static void EnsureGpuUIntBuffer(
        ref XRDataBuffer? buffer,
        string name,
        uint elementCount,
        bool readback)
    {
        uint requiredCount = Math.Max(elementCount, 1u);
        if (buffer is not null && buffer.ElementCount >= requiredCount)
            return;

        buffer?.Dispose();
        buffer = new XRDataBuffer(
            name,
            EBufferTarget.ShaderStorageBuffer,
            requiredCount,
            EComponentType.UInt,
            1,
            false,
            true)
        {
            Usage = readback ? EBufferUsage.StreamRead : EBufferUsage.DynamicDraw,
            Resizable = false,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
            ShouldMap = false,
            RangeFlags = readback ? EBufferMapRangeFlags.Read : 0,
        };
        buffer.SetDataRaw(new uint[requiredCount], checked((int)requiredCount));
        buffer.PushData();
    }

    private static bool EnsureGpuQueryProgram(
        ref XRShader? shader,
        ref XRRenderProgram? program,
        string shaderPath,
        string programName)
    {
        if (shader is null || program is null)
        {
            shader = ShaderHelper.LoadEngineShader(shaderPath, EShaderType.Compute);
            program = new XRRenderProgram(true, false, shader)
            {
                Name = programName,
            };
        }

        if (!program.IsLinked)
            program.Link();
        return program.IsLinked;
    }

    private static unsafe bool TryReadGpuUIntBuffer(XRDataBuffer buffer, Span<uint> destination)
    {
        buffer.MapBufferData();
        try
        {
            List<IApiDataBuffer> mappings = buffer.ActivelyMapping;
            for (int i = 0; i < mappings.Count; i++)
            {
                var address = mappings[i].GetMappedAddress();
                if (!address.HasValue || !address.Value.IsValid)
                    continue;

                new ReadOnlySpan<uint>(address.Value.Pointer, destination.Length).CopyTo(destination);
                Engine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
                Engine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(destination.Length * sizeof(uint));
                return true;
            }
        }
        finally
        {
            buffer.UnmapBufferData();
        }

        return false;
    }

    private RenderableMesh? ResolveGpuMesh()
    {
        if (_targetModel?.Meshes is not { Count: > 0 } meshes)
            return null;

        return meshes[0];
    }

    private void ReleaseGpuWorkload()
    {
        _gpuSceneTree?.Dispose();
        _gpuSceneAabbBuffer?.Dispose();
        _gpuSceneNodeClassesBuffer?.Dispose();
        _gpuSceneQueryResultBuffer?.Dispose();
        _gpuScenePrimitiveClassesBuffer?.Dispose();
        _gpuMeshNodeClassesBuffer?.Dispose();
        _gpuMeshQueryResultBuffer?.Dispose();
        _gpuMeshPrimitiveClassesBuffer?.Dispose();
        _gpuSceneQueryProgram?.Destroy();
        _gpuSceneQueryShader?.Destroy();
        _gpuMeshQueryProgram?.Destroy();
        _gpuMeshQueryShader?.Destroy();
        _gpuSceneTree = null;
        _gpuSceneAabbBuffer = null;
        _gpuSceneAabbs = null;
        _gpuSceneBaseBounds = null;
        _gpuSceneCurrentBounds = null;
        _gpuSceneQueryContainments = null;
        _gpuSceneNodeClassesBuffer = null;
        _gpuSceneQueryResultBuffer = null;
        _gpuScenePrimitiveClassesBuffer = null;
        _gpuScenePrimitiveClassReadback = null;
        _gpuSceneQueryProgram = null;
        _gpuSceneQueryShader = null;
        _gpuMeshNodeClassesBuffer = null;
        _gpuMeshQueryResultBuffer = null;
        _gpuMeshPrimitiveClassesBuffer = null;
        _gpuMeshPrimitiveClassReadback = null;
        _gpuMeshQueryProgram = null;
        _gpuMeshQueryShader = null;
        _gpuMesh = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct GpuSceneAabb(Vector4 min, Vector4 max)
    {
        public readonly Vector4 Min = min;
        public readonly Vector4 Max = max;

        public static GpuSceneAabb FromBounds(in AABB bounds)
            => new(new Vector4(bounds.Min, 0.0f), new Vector4(bounds.Max, 0.0f));
    }
}

using System.Numerics;
using SimpleScene.Util.ssBVH;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.Editor;

public sealed partial class MathBvhTestComponent
{
    private void RenderDebug()
    {
        if (Engine.Rendering.State.IsShadowPass || Engine.Rendering.State.IsLightProbePass)
            return;

        // Vulkan accepts compute work only while a render-graph pass identity is active.
        // Keep this render callback visible even when debug drawing is disabled so benchmark
        // copies continue exercising their GPU BVH without a separate out-of-pass render job.
        using IDisposable passScope = Engine.Rendering.State.PushRenderGraphPassIndex(
            (int)XREngine.Data.Rendering.EDefaultRenderPass.OnTopForward);
        ExecuteQueuedGpuWorkload();

        if (!DebugRenderEnabled)
            return;

        switch (Mode)
        {
            case MathBvhTestMode.CpuScene:
                RenderCpuSceneDebug();
                break;
            case MathBvhTestMode.GpuScene:
                RenderGpuSceneDebug();
                break;
            case MathBvhTestMode.LegacyCpuMesh:
                RenderCpuMeshDebug();
                break;
            case MathBvhTestMode.GpuMesh:
                RenderGpuMeshDebug();
                break;
        }

        if (ShowValidationMarker)
            RenderValidationMarker();
    }

    private void RenderCpuSceneDebug()
    {
        if (_cpuSceneItems is null ||
            _cpuSceneTree is null ||
            _sceneTreeBaseDebugRenderer is null ||
            _sceneTreeQueryDebugRenderer is null)
        {
            return;
        }

        Matrix4x4 localToWorld = Transform.RenderMatrix;
        // Keep the complete tree visible. The second pass below is query-filtered, so only
        // its highlight changes as the animated query crosses node bounds.
        if (RenderBaseNodes)
            _cpuSceneTree.DebugRenderNodes(null, _sceneTreeBaseDebugRenderer);

        if (RenderSourceGeometry)
        {
            for (int i = 0; i < _cpuSceneItems.Length; i++)
            {
                SceneBvhItem item = _cpuSceneItems[i];
                if (item.LocalCullingVolume is not { } bounds)
                    continue;

                if (TryGetGeometryColor(item.QueryContainment, out ColorF4 color))
                    RenderLocalBox(bounds, localToWorld, color);
            }
        }

        if (RenderVisitedNodes)
            _cpuSceneTree.DebugRenderNodes(_queryVolume, _sceneTreeQueryDebugRenderer);
        if (RenderQuery)
            RenderQueryVolume(localToWorld);
    }

    private void RenderCpuSceneTreeBaseNode(
        Vector3 halfExtents,
        Vector3 center,
        EContainment _,
        bool isLeaf)
    {
        if (ShouldRenderNode(isLeaf))
            RenderLocalBox(
                AABB.FromCenterSize(center, halfExtents * 2.0f),
                Transform.RenderMatrix,
                isLeaf ? LeafNodeColor : InternalNodeColor);
    }

    private void RenderCpuSceneTreeQueryNode(
        Vector3 halfExtents,
        Vector3 center,
        EContainment _,
        bool isLeaf)
    {
        if (!ShouldRenderNode(isLeaf))
            return;

        RenderLocalBox(
            AABB.FromCenterSize(center, halfExtents * 2.0f),
            Transform.RenderMatrix,
            VisitedNodeColor);
    }

    private void RenderGpuSceneDebug()
    {
        if (_gpuSceneCurrentBounds is not null && RenderSourceGeometry)
        {
            Matrix4x4 localToWorld = Transform.RenderMatrix;
            for (int i = 0; i < _gpuSceneCurrentBounds.Length; i++)
            {
                EContainment containment = _gpuSceneQueryContainments is not null &&
                    (uint)i < (uint)_gpuSceneQueryContainments.Length
                        ? _gpuSceneQueryContainments[i]
                        : EContainment.Disjoint;
                if (TryGetGeometryColor(containment, out ColorF4 color))
                    RenderLocalBox(_gpuSceneCurrentBounds[i], localToWorld, color);
            }
        }
        if (RenderQuery)
            RenderQueryVolume(Transform.RenderMatrix);

        if (_gpuSceneTree?.NodeBuffer is not { } nodeBuffer || !WorkloadReady)
        {
            return;
        }

        uint showFilter = GetGpuNodeShowFilter();
        uint maxNodes = (uint)MaxDebugNodes;
        if (RenderBaseNodes)
        {
            _gpuDebugRenderer.Queue(
                nodeBuffer,
                _gpuSceneTree.NodeCount,
                Transform.RenderMatrix,
                maxNodes,
                BaseNodeLineWidth,
                ToVector4(LeafNodeColor),
                ToVector4(InternalNodeColor),
                showFilter);
        }
        if (RenderVisitedNodes && _gpuSceneNodeClassesBuffer is not null)
        {
            _gpuDebugRenderer.Queue(
                nodeBuffer,
                _gpuSceneTree.NodeCount,
                Transform.RenderMatrix,
                maxNodes,
                VisitedNodeLineWidth,
                Vector4.Zero,
                Vector4.Zero,
                showFilter,
                new GpuBvhDebugNodeClassOptions(
                    _gpuSceneNodeClassesBuffer,
                    GpuBvhDebugNodeClassMode.ClassifiedOnly,
                    ToVector4(VisitedNodeColor),
                    ToVector4(VisitedNodeColor),
                    0b11u),
                GpuBvhDebugOverlayLayer.Highlight);
        }
    }

    private void RenderCpuMeshDebug()
    {
        if (_cpuMeshNodes is null || _cpuMeshVisitedNodes is null)
            return;

        Matrix4x4 localToWorld = _targetModel?.Transform.RenderMatrix ?? Transform.RenderMatrix;
        if (RenderSourceGeometry)
            RenderSourceMeshWireframe(localToWorld, DisjointGeometryColor);
        if (RenderBaseNodes)
        {
            for (int i = 0; i < _cpuMeshNodes.Count; i++)
            {
                BVHNode<Triangle> node = _cpuMeshNodes[i];
                if (ShouldRenderNode(node.IsLeaf))
                    RenderLocalBox(node.box, localToWorld, node.IsLeaf ? LeafNodeColor : InternalNodeColor);
            }
        }

        if (RenderVisitedNodes)
        {
            for (int i = 0; i < _cpuMeshNodes.Count; i++)
            {
                BVHNode<Triangle> node = _cpuMeshNodes[i];
                bool visited = (uint)node.nodeNumber < (uint)_cpuMeshVisitedNodes.Length && _cpuMeshVisitedNodes[node.nodeNumber];
                if (!visited || !ShouldRenderNode(node.IsLeaf))
                    continue;

                RenderLocalBox(node.box, localToWorld, VisitedNodeColor);
            }
        }

        if (RenderQuery && QueryShape == MathBvhQueryShape.Raycast)
        {
            ColorF4 rayColor = ValidationPassed ? QueryColor : QueryFailureColor;
            RenderLocalLine(_meshQuerySegment.Start, _meshQuerySegment.End, localToWorld, rayColor);
        }
        else if (RenderQuery)
        {
            RenderQueryVolume(localToWorld);
        }
        if (RenderQueryResults)
            RenderMeshQueryResults(localToWorld);
        if (RenderHitMarker && QueryShape == MathBvhQueryShape.Raycast && _lastMeshHitPoint is { } hitPoint)
            RenderLocalPoint(hitPoint, localToWorld, HitMarkerColor);
    }

    private void RenderGpuMeshDebug()
    {
        GpuMeshBvh? bvh = _gpuMesh?.GpuMeshBvh;
        Matrix4x4 localToWorld = bvh?.LocalToWorldMatrix ?? (_targetModel?.Transform.RenderMatrix ?? Transform.RenderMatrix);
        if (RenderSourceGeometry)
            RenderSourceMeshWireframe(localToWorld, DisjointGeometryColor);

        if (bvh?.BvhNodeBuffer is not { } nodeBuffer ||
            !WorkloadReady ||
            !bvh.IsBvhReady)
        {
            return;
        }

        uint showFilter = GetGpuNodeShowFilter();
        uint maxNodes = (uint)MaxDebugNodes;
        if (RenderBaseNodes)
        {
            _gpuDebugRenderer.Queue(
                nodeBuffer,
                bvh.BvhNodeCount,
                bvh.LocalToWorldMatrix,
                maxNodes,
                BaseNodeLineWidth,
                ToVector4(LeafNodeColor),
                ToVector4(InternalNodeColor),
                showFilter);
        }
        if (RenderVisitedNodes && _gpuMeshNodeClassesBuffer is not null)
        {
            _gpuDebugRenderer.Queue(
                nodeBuffer,
                bvh.BvhNodeCount,
                bvh.LocalToWorldMatrix,
                maxNodes,
                VisitedNodeLineWidth,
                Vector4.Zero,
                Vector4.Zero,
                showFilter,
                new GpuBvhDebugNodeClassOptions(
                    _gpuMeshNodeClassesBuffer,
                    GpuBvhDebugNodeClassMode.ClassifiedOnly,
                    ToVector4(VisitedNodeColor),
                    ToVector4(VisitedNodeColor),
                    0b11u),
                GpuBvhDebugOverlayLayer.Highlight);
        }

        if (RenderQuery && QueryShape == MathBvhQueryShape.Raycast)
        {
            ColorF4 rayColor = ValidationPassed ? QueryColor : QueryFailureColor;
            RenderLocalLine(_meshQuerySegment.Start, _meshQuerySegment.End, localToWorld, rayColor);
        }
        else if (RenderQuery)
        {
            RenderQueryVolume(localToWorld);
        }
        if (RenderQueryResults)
            RenderMeshQueryResults(localToWorld);
        if (RenderHitMarker && QueryShape == MathBvhQueryShape.Raycast && _lastMeshHitPoint is { } hitPoint)
            RenderLocalPoint(hitPoint, localToWorld, HitMarkerColor);
    }

    private void RenderSourceMeshWireframe(in Matrix4x4 localToWorld, ColorF4 color)
    {
        if (_sourceTriangles is null)
            return;

        for (int i = 0; i < _sourceTriangles.Count; i++)
        {
            Triangle triangle = _sourceTriangles[i];
            RenderLocalLine(triangle.A, triangle.B, localToWorld, color);
            RenderLocalLine(triangle.B, triangle.C, localToWorld, color);
            RenderLocalLine(triangle.C, triangle.A, localToWorld, color);
        }
    }

    private void RenderQueryVolume(in Matrix4x4 localToWorld)
    {
        switch (_queryVolume.Shape)
        {
            case MathBvhQueryShape.Sphere:
                RenderLocalSphere(_queryVolume.Sphere, localToWorld, QueryColor);
                break;
            case MathBvhQueryShape.Frustum:
                RenderLocalFrustum(_queryVolume.Frustum, localToWorld, QueryColor);
                break;
            case MathBvhQueryShape.Raycast:
                RenderLocalLine(_queryVolume.Raycast.Start, _queryVolume.Raycast.End, localToWorld, QueryColor);
                break;
            default:
                RenderLocalBox(_queryVolume.Box, localToWorld, QueryColor);
                break;
        }
    }

    private void RenderMeshQueryResults(in Matrix4x4 localToWorld)
    {
        if (_sourceTriangles is null)
            return;

        uint primitiveMask = GetMeshQueryPrimitiveMask();
        for (int triangleIndex = 0; triangleIndex < _sourceTriangles.Count; triangleIndex++)
        {
            Triangle triangle = _sourceTriangles[triangleIndex];
            if ((primitiveMask & 0b100u) != 0u && _meshTriangleQueryContainments is not null)
            {
                RenderClassifiedLine(
                    triangle.A,
                    triangle.B,
                    _meshTriangleQueryContainments[triangleIndex],
                    localToWorld);
                RenderClassifiedLine(
                    triangle.B,
                    triangle.C,
                    _meshTriangleQueryContainments[triangleIndex],
                    localToWorld);
                RenderClassifiedLine(
                    triangle.C,
                    triangle.A,
                    _meshTriangleQueryContainments[triangleIndex],
                    localToWorld);
            }

            int elementOffset = triangleIndex * 3;
            if ((primitiveMask & 0b010u) != 0u && _meshLineQueryContainments is not null)
            {
                RenderClassifiedLine(
                    triangle.A,
                    triangle.B,
                    _meshLineQueryContainments[elementOffset],
                    localToWorld);
                RenderClassifiedLine(
                    triangle.B,
                    triangle.C,
                    _meshLineQueryContainments[elementOffset + 1],
                    localToWorld);
                RenderClassifiedLine(
                    triangle.C,
                    triangle.A,
                    _meshLineQueryContainments[elementOffset + 2],
                    localToWorld);
            }

            if ((primitiveMask & 0b001u) == 0u || _meshPointQueryContainments is null)
                continue;
            RenderClassifiedPoint(triangle.A, _meshPointQueryContainments[elementOffset], localToWorld);
            RenderClassifiedPoint(triangle.B, _meshPointQueryContainments[elementOffset + 1], localToWorld);
            RenderClassifiedPoint(triangle.C, _meshPointQueryContainments[elementOffset + 2], localToWorld);
        }
    }

    private void RenderClassifiedLine(
        Vector3 start,
        Vector3 end,
        EContainment containment,
        in Matrix4x4 localToWorld)
    {
        if (TryGetGeometryColor(containment, out ColorF4 color))
            RenderLocalLine(start, end, localToWorld, color);
    }

    private void RenderClassifiedPoint(
        Vector3 point,
        EContainment containment,
        in Matrix4x4 localToWorld)
    {
        if (TryGetGeometryColor(containment, out ColorF4 color))
            RenderLocalPoint(point, localToWorld, color);
    }

    private bool TryGetGeometryColor(EContainment containment, out ColorF4 color)
    {
        color = containment switch
        {
            EContainment.Contains => ContainedGeometryColor,
            EContainment.Intersects => IntersectedGeometryColor,
            _ => DisjointGeometryColor,
        };
        return containment != EContainment.Intersects || RenderIntersectedGeometry;
    }

    private static void RenderLocalSphere(in Sphere sphere, in Matrix4x4 localToWorld, ColorF4 color)
    {
        float scale = MathF.Max(
            new Vector3(localToWorld.M11, localToWorld.M12, localToWorld.M13).Length(),
            MathF.Max(
                new Vector3(localToWorld.M21, localToWorld.M22, localToWorld.M23).Length(),
                new Vector3(localToWorld.M31, localToWorld.M32, localToWorld.M33).Length()));
        Engine.Rendering.Debug.RenderSphere(
            Vector3.Transform(sphere.Center, localToWorld),
            sphere.Radius * scale,
            solid: false,
            color);
    }

    private static void RenderLocalFrustum(in Frustum frustum, in Matrix4x4 localToWorld, ColorF4 color)
    {
        RenderLocalLine(frustum.LeftBottomNear, frustum.RightBottomNear, localToWorld, color);
        RenderLocalLine(frustum.RightBottomNear, frustum.RightTopNear, localToWorld, color);
        RenderLocalLine(frustum.RightTopNear, frustum.LeftTopNear, localToWorld, color);
        RenderLocalLine(frustum.LeftTopNear, frustum.LeftBottomNear, localToWorld, color);
        RenderLocalLine(frustum.LeftBottomFar, frustum.RightBottomFar, localToWorld, color);
        RenderLocalLine(frustum.RightBottomFar, frustum.RightTopFar, localToWorld, color);
        RenderLocalLine(frustum.RightTopFar, frustum.LeftTopFar, localToWorld, color);
        RenderLocalLine(frustum.LeftTopFar, frustum.LeftBottomFar, localToWorld, color);
        RenderLocalLine(frustum.LeftBottomNear, frustum.LeftBottomFar, localToWorld, color);
        RenderLocalLine(frustum.RightBottomNear, frustum.RightBottomFar, localToWorld, color);
        RenderLocalLine(frustum.LeftTopNear, frustum.LeftTopFar, localToWorld, color);
        RenderLocalLine(frustum.RightTopNear, frustum.RightTopFar, localToWorld, color);
    }

    private void RenderValidationMarker()
    {
        ColorF4 color = WorkloadReady
            ? ValidationPassed ? ValidationPassedColor : ValidationFailedColor
            : ValidationPendingColor;
        Vector3 worldPosition = Vector3.Transform(new Vector3(0.0f, 6.3f, 0.0f), Transform.RenderMatrix);
        Engine.Rendering.Debug.RenderSphere(worldPosition, 0.18f, solid: true, color);
    }

    private static void RenderLocalBox(in AABB bounds, in Matrix4x4 localToWorld, ColorF4 color)
    {
        Matrix4x4 orientation = localToWorld;
        orientation.M41 = 0.0f;
        orientation.M42 = 0.0f;
        orientation.M43 = 0.0f;
        Vector3 worldCenter = Vector3.Transform(bounds.Center, localToWorld);
        Engine.Rendering.Debug.RenderBox(bounds.HalfExtents, worldCenter, orientation, solid: false, color);
    }

    private static void RenderLocalLine(Vector3 localStart, Vector3 localEnd, in Matrix4x4 localToWorld, ColorF4 color)
        => Engine.Rendering.Debug.RenderLine(
            Vector3.Transform(localStart, localToWorld),
            Vector3.Transform(localEnd, localToWorld),
            color);

    private static void RenderLocalPoint(Vector3 localPoint, in Matrix4x4 localToWorld, ColorF4 color)
        => Engine.Rendering.Debug.RenderPoint(Vector3.Transform(localPoint, localToWorld), color);

    private static Vector4 ToVector4(ColorF4 color)
        => new(color.R, color.G, color.B, color.A);
}

using System.Drawing;
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

        RenderValidationMarker();
    }

    private void RenderCpuSceneDebug()
    {
        if (_cpuSceneItems is null || _cpuSceneTree is null || _sceneTreeDebugRenderer is null)
            return;

        Matrix4x4 localToWorld = Transform.RenderMatrix;
        for (int i = 0; i < _cpuSceneItems.Length; i++)
        {
            SceneBvhItem item = _cpuSceneItems[i];
            if (item.LocalCullingVolume is not { } bounds)
                continue;

            ColorF4 color = item.QueryHit
                ? new ColorF4(0.15f, 1.0f, 0.35f, 0.95f)
                : new ColorF4(0.35f, 0.42f, 0.50f, 0.45f);
            RenderLocalBox(bounds, localToWorld, color);
        }

        _cpuSceneTree.DebugRender(_sceneQueryBounds, _sceneTreeDebugRenderer);
        RenderLocalBox(_sceneQueryBounds, localToWorld, new ColorF4(1.0f, 0.82f, 0.12f, 1.0f));
    }

    private void RenderCpuSceneTreeNode(Vector3 halfExtents, Vector3 center, Color color)
    {
        ColorF4 nodeColor = color == Color.Green
            ? new ColorF4(0.18f, 0.82f, 1.0f, 0.9f)
            : new ColorF4(0.48f, 0.65f, 1.0f, 0.55f);
        RenderLocalBox(AABB.FromCenterSize(center, halfExtents * 2.0f), Transform.RenderMatrix, nodeColor);
    }

    private void RenderGpuSceneDebug()
    {
        if (_gpuSceneCurrentBounds is not null)
        {
            Matrix4x4 localToWorld = Transform.RenderMatrix;
            for (int i = 0; i < _gpuSceneCurrentBounds.Length; i++)
                RenderLocalBox(_gpuSceneCurrentBounds[i], localToWorld, new ColorF4(0.32f, 0.39f, 0.48f, 0.35f));
        }

        if (_gpuSceneTree?.NodeBuffer is not { } nodeBuffer || !WorkloadReady)
            return;

        _gpuDebugRenderer.Queue(
            nodeBuffer,
            _gpuSceneTree.NodeCount,
            Transform.RenderMatrix,
            MaxDebugNodeCount,
            0.0015f,
            new Vector4(0.16f, 1.0f, 0.38f, 0.95f),
            new Vector4(1.0f, 0.56f, 0.08f, 0.62f),
            0u);
    }

    private void RenderCpuMeshDebug()
    {
        if (_cpuMeshNodes is null || _cpuMeshVisitedNodes is null)
            return;

        Matrix4x4 localToWorld = _targetModel?.Transform.RenderMatrix ?? Transform.RenderMatrix;
        RenderSourceMeshWireframe(localToWorld, new ColorF4(0.25f, 0.48f, 0.62f, 0.38f));
        for (int i = 0; i < _cpuMeshNodes.Count; i++)
        {
            BVHNode<Triangle> node = _cpuMeshNodes[i];
            bool visited = (uint)node.nodeNumber < (uint)_cpuMeshVisitedNodes.Length && _cpuMeshVisitedNodes[node.nodeNumber];
            ColorF4 color = visited
                ? new ColorF4(1.0f, 0.78f, 0.08f, 0.95f)
                : node.IsLeaf
                    ? new ColorF4(0.20f, 1.0f, 0.42f, 0.65f)
                    : new ColorF4(0.20f, 0.68f, 1.0f, 0.42f);
            RenderLocalBox(node.box, localToWorld, color);
        }

        ColorF4 rayColor = ValidationPassed ? ColorF4.LightGreen : ColorF4.Red;
        RenderLocalLine(_meshQuerySegment.Start, _meshQuerySegment.End, localToWorld, rayColor);
        if (_lastMeshHitPoint is { } hitPoint)
            RenderLocalPoint(hitPoint, localToWorld, ColorF4.Yellow);
    }

    private void RenderGpuMeshDebug()
    {
        GpuMeshBvh? bvh = _gpuMesh?.GpuMeshBvh;
        Matrix4x4 localToWorld = bvh?.LocalToWorldMatrix ?? (_targetModel?.Transform.RenderMatrix ?? Transform.RenderMatrix);
        RenderSourceMeshWireframe(localToWorld, new ColorF4(0.46f, 0.24f, 0.60f, 0.38f));

        if (bvh?.BvhNodeBuffer is not { } nodeBuffer || !WorkloadReady || !bvh.IsBvhReady)
            return;

        _gpuDebugRenderer.Queue(
            nodeBuffer,
            bvh.BvhNodeCount,
            bvh.LocalToWorldMatrix,
            MaxDebugNodeCount,
            0.0015f,
            new Vector4(0.12f, 1.0f, 0.62f, 0.95f),
            new Vector4(0.90f, 0.18f, 1.0f, 0.58f),
            0u);
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

    private void RenderValidationMarker()
    {
        ColorF4 color = WorkloadReady
            ? ValidationPassed ? ColorF4.LightGreen : ColorF4.Red
            : ColorF4.Orange;
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
}

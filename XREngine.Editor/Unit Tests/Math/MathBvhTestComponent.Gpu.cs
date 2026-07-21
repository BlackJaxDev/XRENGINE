using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.Editor;

public sealed partial class MathBvhTestComponent
{
    private GpuBvhTree? _gpuSceneTree;
    private XRDataBuffer? _gpuSceneAabbBuffer;
    private GpuSceneAabb[]? _gpuSceneAabbs;
    private AABB[]? _gpuSceneBaseBounds;
    private AABB[]? _gpuSceneCurrentBounds;
    private int _gpuSceneMoveCursor;

    private RenderableMesh? _gpuMesh;
    private bool _gpuMeshEverReady;

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

        int movedIndex = _gpuSceneMoveCursor++ % _gpuSceneAabbs.Length;
        AABB baseBounds = _gpuSceneBaseBounds[movedIndex];
        float time = (float)Engine.ElapsedTime;
        Vector3 offset = new(
            MathF.Sin(time * 0.73f + movedIndex * 0.31f) * 0.28f,
            MathF.Cos(time * 0.91f + movedIndex * 0.17f) * 0.18f,
            MathF.Sin(time * 0.57f + movedIndex * 0.23f) * 0.24f);
        AABB movedBounds = AABB.FromCenterSize(baseBounds.Center + offset, baseBounds.Size);
        _gpuSceneCurrentBounds[movedIndex] = movedBounds;
        GpuSceneAabb gpuBounds = GpuSceneAabb.FromBounds(movedBounds);
        _gpuSceneAabbs[movedIndex] = gpuBounds;
        _gpuSceneAabbBuffer.SetDataRawAtIndex((uint)movedIndex, gpuBounds);
        uint stride = (uint)Marshal.SizeOf<GpuSceneAabb>();
        _gpuSceneAabbBuffer.PushSubData(checked((int)((uint)movedIndex * stride)), stride);

        if (_gpuSceneTree.IsDirty || _gpuSceneTree.NodeCount == 0u)
            _gpuSceneTree.Build(_gpuSceneAabbBuffer, (uint)_gpuSceneAabbs.Length, s_sceneTreeBounds);
        else
            _gpuSceneTree.Refit();

        GpuBvhDiagnostics diagnostics = _gpuSceneTree.Diagnostics;
        Interlocked.Exchange(ref _buildOperationCount, checked((long)diagnostics.BuildCount));
        Interlocked.Exchange(ref _updateOperationCount, checked((long)diagnostics.RefitCount));

        uint expectedNodes = CalculateBinaryBvhNodeCount((uint)_gpuSceneAabbs.Length, SceneLeafCapacity);
        bool ready = !_gpuSceneTree.IsDirty && _gpuSceneTree.NodeBuffer is not null;
        SetValidationState(
            ready,
            passed: ready && _gpuSceneTree.PrimitiveCount == (uint)_gpuSceneAabbs.Length && _gpuSceneTree.NodeCount == expectedNodes);
    }

    private void EnsureGpuSceneResources()
    {
        if (_gpuSceneTree is not null && _gpuSceneAabbBuffer is not null)
            return;

        _gpuSceneBaseBounds = CreateScenePrimitiveBounds();
        _gpuSceneCurrentBounds = (AABB[])_gpuSceneBaseBounds.Clone();
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
        bool valid =
            triangleCount == (uint)(_sourceTriangles?.Count ?? 0) &&
            bvh.BvhNodeCount == expectedNodes &&
            bvh.BvhNodeBuffer is not null &&
            bvh.PackedTriangleBuffer is not null;
        SetValidationState(ready: true, passed: valid);
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
        _gpuSceneTree = null;
        _gpuSceneAabbBuffer = null;
        _gpuSceneAabbs = null;
        _gpuSceneBaseBounds = null;
        _gpuSceneCurrentBounds = null;
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

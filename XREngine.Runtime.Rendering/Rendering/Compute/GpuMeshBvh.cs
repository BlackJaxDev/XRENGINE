using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Compute;

/// <summary>
/// GPU triangle BVH owned by a single renderable mesh instance.
/// </summary>
public sealed class GpuMeshBvh : IDisposable, IGpuBvhProvider
{
    private const string TriangleAabbShaderPath = "Scene3D/RenderPipeline/mesh_triangle_aabbs.comp";
    private const uint TriangleAabbGroupSize = 128u;

    private readonly GpuBvhTree _tree = new();

    private XRDataBuffer? _aabbBuffer;
    private XRDataBuffer? _triangleIndexBuffer;
    private XRShader? _triangleAabbShader;
    private XRRenderProgram? _triangleAabbProgram;
    private TriangleGpuIndex[]? _triangleIndices;
    private TriangleAabb[]? _staticAabbs;

    private XRMesh? _sourceMesh;
    private XRMeshRenderer? _sourceRenderer;
    private uint _triangleCount;
    private bool _built;
    private bool _staticAabbsUploaded;

    public XRDataBuffer? BvhNodeBuffer => _tree.NodeBuffer;
    public XRDataBuffer? BvhRangeBuffer => _tree.RangeBuffer;
    public XRDataBuffer? BvhMortonBuffer => _tree.MortonBuffer;
    public uint BvhNodeCount => _tree.NodeCount;
    public bool IsBvhReady => _built && _tree.NodeCount > 0 && _tree.PrimitiveCount == _triangleCount;
    public Matrix4x4 LocalToWorldMatrix { get; private set; } = Matrix4x4.Identity;
    public bool LastUpdateUsedGpuSkinning { get; private set; }

    public bool MatchesSource(XRMeshRenderer? renderer)
    {
        XRMesh? mesh = renderer?.Mesh;
        uint triangleCount = (uint)(mesh?.Triangles?.Count ?? 0);
        return ReferenceEquals(_sourceRenderer, renderer) &&
            ReferenceEquals(_sourceMesh, mesh) &&
            _triangleCount == triangleCount;
    }

    public void MarkDirty()
    {
        _built = false;
        _staticAabbsUploaded = false;
        _tree.MarkDirty();
    }

    public bool Prepare(RenderableMesh renderable, bool realtimeSkinned, bool forceRebuild = false)
    {
        if (renderable is null || !RuntimeEngine.IsRenderThread || AbstractRenderer.Current is null)
            return false;

        XRMeshRenderer? renderer = renderable.CurrentLODRenderer;
        XRMesh? mesh = renderer?.Mesh;
        if (renderer is null || mesh?.Triangles is not { Count: > 0 } triangles)
            return ClearReadyState();

        uint triangleCount = (uint)triangles.Count;
        bool sourceChanged = !ReferenceEquals(_sourceMesh, mesh) || !ReferenceEquals(_sourceRenderer, renderer) || _triangleCount != triangleCount;
        if (sourceChanged)
            ResetForSource(mesh, renderer, triangleCount);

        ConfigureTree();
        EnsureTriangleIndexBuffer(mesh, triangles);
        EnsureAabbBuffer(triangleCount);

        bool skinned = realtimeSkinned && mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning;
        if (skinned)
        {
            if (TryUpdateSkinnedAabbs(renderable, renderer, mesh, triangleCount))
            {
                LastUpdateUsedGpuSkinning = true;
                LocalToWorldMatrix = renderable.SkinnedBvhLocalToWorldMatrix;
                return BuildOrRefit(
                    mesh,
                    triangleCount,
                    renderable.RenderInfo.LocalCullingVolume ?? mesh.Bounds,
                    forceRebuild,
                    aabbsUpdated: true);
            }

            if (IsBvhReady)
                return IsBvhReady;

            // Compute-skinned buffers can be unavailable when compute skinning is disabled
            // or the prepass has not produced output yet. Keep GPU preview visible by
            // building a bind-pose GPU BVH once, then refit it later when skinned buffers appear.
            LastUpdateUsedGpuSkinning = false;
            LocalToWorldMatrix = renderable.Component.Transform.RenderMatrix;
            bool fallbackAabbsWillUpload = !_staticAabbsUploaded;
            if (!UploadStaticAabbsIfNeeded(mesh, triangles, triangleCount))
                return false;

            return BuildOrRefit(mesh, triangleCount, mesh.Bounds, forceRebuild, fallbackAabbsWillUpload);
        }

        LastUpdateUsedGpuSkinning = false;
        LocalToWorldMatrix = renderable.Component.Transform.RenderMatrix;
        bool staticAabbsWillUpload = !_staticAabbsUploaded;
        if (!UploadStaticAabbsIfNeeded(mesh, triangles, triangleCount))
            return false;

        return BuildOrRefit(mesh, triangleCount, mesh.Bounds, forceRebuild, staticAabbsWillUpload);
    }

    private bool ClearReadyState()
    {
        _built = false;
        _triangleCount = 0;
        _tree.Clear();
        return false;
    }

    private void ResetForSource(XRMesh mesh, XRMeshRenderer renderer, uint triangleCount)
    {
        _sourceMesh = mesh;
        _sourceRenderer = renderer;
        _triangleCount = triangleCount;
        _built = false;
        _staticAabbsUploaded = false;
        _tree.MarkDirty();
    }

    private void ConfigureTree()
    {
        _tree.BuildMode = BvhBuildMode.MortonOnly;
        _tree.MaxLeafPrimitives = 1u;
    }

    private void EnsureTriangleIndexBuffer(XRMesh mesh, List<IndexTriangle> triangles)
    {
        if (_triangleIndexBuffer is not null && _triangleIndexBuffer.ElementCount >= (uint)triangles.Count && !_tree.IsDirty)
            return;

        if (_triangleIndices is null || _triangleIndices.Length < triangles.Count)
            _triangleIndices = new TriangleGpuIndex[triangles.Count];

        for (int i = 0; i < triangles.Count; i++)
        {
            IndexTriangle tri = triangles[i];
            _triangleIndices[i] = new TriangleGpuIndex(
                (uint)Math.Max(0, tri.Point0),
                (uint)Math.Max(0, tri.Point1),
                (uint)Math.Max(0, tri.Point2),
                0u);
        }

        _triangleIndexBuffer ??= new XRDataBuffer(
            "GpuMeshBvh_TriangleIndices",
            EBufferTarget.ShaderStorageBuffer,
            Math.Max((uint)triangles.Count, 1u),
            EComponentType.Struct,
            (uint)Marshal.SizeOf<TriangleGpuIndex>(),
            false,
            true)
        {
            Usage = EBufferUsage.DynamicDraw,
            Resizable = true,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
            ShouldMap = false
        };

        if (_triangleIndexBuffer.ElementCount < (uint)triangles.Count)
            _triangleIndexBuffer.Resize((uint)triangles.Count, false, true);

        _triangleIndexBuffer.SetDataRaw(new ReadOnlySpan<TriangleGpuIndex>(_triangleIndices, 0, triangles.Count));
        _triangleIndexBuffer.PushData();
    }

    private void EnsureAabbBuffer(uint triangleCount)
    {
        if (_aabbBuffer is null)
        {
            _aabbBuffer = new XRDataBuffer(
                "GpuMeshBvh_TriangleAabbs",
                EBufferTarget.ShaderStorageBuffer,
                Math.Max(triangleCount, 1u),
                EComponentType.Struct,
                (uint)Marshal.SizeOf<TriangleAabb>(),
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                Resizable = true,
                DisposeOnPush = false,
                PadEndingToVec4 = true,
                ShouldMap = false
            };
        }
        else if (_aabbBuffer.ElementCount < triangleCount)
        {
            _aabbBuffer.Resize(triangleCount, false, true);
        }
    }

    private bool TryUpdateSkinnedAabbs(RenderableMesh renderable, XRMeshRenderer renderer, XRMesh mesh, uint triangleCount)
    {
        SkinningPrepassDispatcher.Instance.RunForGpuMeshBvh(renderer);
        var (positions, _, _, interleaved) = SkinningPrepassDispatcher.Instance.GetSkinnedBuffers(renderer);
        XRDataBuffer? source = positions ?? interleaved;
        if (source is null)
            return false;

        if (!EnsureTriangleAabbProgramReady())
            return false;

        XRRenderProgram program = _triangleAabbProgram!;
        program.BindBuffer(positions ?? source, 0);
        program.BindBuffer(interleaved ?? source, 1);
        program.BindBuffer(_triangleIndexBuffer!, 2);
        program.BindBuffer(_aabbBuffer!, 3);
        program.Uniform("TriangleCount", triangleCount);
        program.Uniform("UseInterleaved", interleaved is not null ? 1u : 0u);
        program.Uniform("InterleavedStrideBytes", mesh.InterleavedStride);
        program.Uniform("PositionOffsetBytes", mesh.PositionOffset);
        program.Uniform("ApplyTransform", 1);
        program.Uniform("TransformMatrix", renderable.SkinnedBvhWorldToLocalMatrix);
        program.DispatchCompute(
            ComputeGroups(triangleCount, TriangleAabbGroupSize),
            1u,
            1u,
            EMemoryBarrierMask.ShaderStorage);

        return true;
    }

    private bool UploadStaticAabbsIfNeeded(XRMesh mesh, List<IndexTriangle> triangles, uint triangleCount)
    {
        if (_staticAabbsUploaded)
            return true;

        if (_staticAabbs is null || _staticAabbs.Length < triangles.Count)
            _staticAabbs = new TriangleAabb[triangles.Count];

        for (int i = 0; i < triangles.Count; i++)
        {
            IndexTriangle tri = triangles[i];
            Vector3 p0 = mesh.GetPosition((uint)Math.Max(0, tri.Point0));
            Vector3 p1 = mesh.GetPosition((uint)Math.Max(0, tri.Point1));
            Vector3 p2 = mesh.GetPosition((uint)Math.Max(0, tri.Point2));
            Vector3 min = Vector3.Min(p0, Vector3.Min(p1, p2));
            Vector3 max = Vector3.Max(p0, Vector3.Max(p1, p2));
            _staticAabbs[i] = new TriangleAabb(new Vector4(min, 0.0f), new Vector4(max, 0.0f));
        }

        _aabbBuffer!.SetDataRaw(new ReadOnlySpan<TriangleAabb>(_staticAabbs, 0, (int)triangleCount));
        _aabbBuffer.PushData();
        _staticAabbsUploaded = true;
        return true;
    }

    private bool BuildOrRefit(XRMesh mesh, uint triangleCount, AABB bounds, bool forceRebuild, bool aabbsUpdated)
    {
        if (_aabbBuffer is null || triangleCount == 0)
            return false;

        if (!_built || forceRebuild || _tree.IsDirty || _tree.PrimitiveCount != triangleCount)
        {
            _tree.Build(_aabbBuffer, triangleCount, NormalizeBounds(bounds, mesh.Bounds));
            _built = _tree.NodeCount > 0 && _tree.PrimitiveCount == triangleCount;
            return _built;
        }

        if (aabbsUpdated)
            _tree.Refit();

        _built = _tree.NodeCount > 0 && _tree.PrimitiveCount == triangleCount;
        return _built;
    }

    private static AABB NormalizeBounds(AABB preferred, AABB fallback)
        => preferred.IsValid ? preferred : fallback;

    private bool EnsureTriangleAabbProgramReady()
    {
        if (_triangleAabbProgram is null)
        {
            _triangleAabbShader ??= ShaderHelper.LoadEngineShader(TriangleAabbShaderPath, EShaderType.Compute);
            _triangleAabbProgram = new XRRenderProgram(true, false, _triangleAabbShader);
        }

        if (_triangleAabbProgram.IsLinked)
            return true;

        _triangleAabbProgram.Link();

        return _triangleAabbProgram.IsLinked;
    }

    private static uint ComputeGroups(uint count, uint localSize)
        => Math.Max(1u, (count + localSize - 1u) / localSize);

    public void Dispose()
    {
        _tree.Dispose();
        _aabbBuffer?.Dispose();
        _triangleIndexBuffer?.Dispose();
        _triangleAabbProgram?.Destroy();
        _triangleAabbShader?.Destroy();
        _aabbBuffer = null;
        _triangleIndexBuffer = null;
        _triangleAabbProgram = null;
        _triangleAabbShader = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TriangleGpuIndex(uint x, uint y, uint z, uint w)
    {
        public readonly uint X = x;
        public readonly uint Y = y;
        public readonly uint Z = z;
        public readonly uint W = w;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TriangleAabb(Vector4 min, Vector4 max)
    {
        public readonly Vector4 Min = min;
        public readonly Vector4 Max = max;
    }
}

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
    private const string PackedTriangleShaderPath = "Scene3D/RenderPipeline/mesh_bvh_pack_triangles.comp";
    private const uint TriangleAabbGroupSize = 128u;
    private const uint PackedTriangleGroupSize = 128u;
    public const uint DefaultMaxLeafPrimitives = 1u;

    private readonly GpuBvhTree _tree = new("GpuMeshBvh");

    private XRDataBuffer? _aabbBuffer;
    private XRDataBuffer? _triangleIndexBuffer;
    private XRDataBuffer? _packedTriangleBuffer;
    private XRDataBuffer? _storagePositionSource;
    private XRDataBuffer? _storagePositionBuffer;
    private XRDataBuffer? _storageInterleavedSource;
    private XRDataBuffer? _storageInterleavedBuffer;
    private XRShader? _triangleAabbShader;
    private XRShader? _packedTriangleShader;
    private XRRenderProgram? _triangleAabbProgram;
    private XRRenderProgram? _packedTriangleProgram;
    private TriangleGpuIndex[]? _triangleIndices;
    private TriangleAabb[]? _staticAabbs;
    private XRDataBuffer? _staticGeometrySource;
    private ulong _staticGeometryRevision;

    private XRMesh? _sourceMesh;
    private XRMeshRenderer? _sourceRenderer;
    private uint _triangleCount;
    private bool _built;
    private bool _staticAabbsUploaded;
    private bool _packedTrianglesUploaded;

    public XRDataBuffer? BvhNodeBuffer => _tree.NodeBuffer;
    public XRDataBuffer? BvhRangeBuffer => _tree.RangeBuffer;
    public XRDataBuffer? BvhMortonBuffer => _tree.MortonBuffer;
    public XRDataBuffer? PackedTriangleBuffer => _packedTriangleBuffer;
    public uint BvhNodeCount => _tree.NodeCount;
    public uint TriangleCount => _triangleCount;
    public uint MaxLeafPrimitives => _tree.MaxLeafPrimitives;
    public bool IsBvhReady => _built &&
        _tree.NodeCount > 0 &&
        _tree.PrimitiveCount == _triangleCount &&
        _packedTrianglesUploaded &&
        _packedTriangleBuffer is not null &&
        _packedTriangleBuffer.ElementCount >= _triangleCount;
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
        _packedTrianglesUploaded = false;
        _tree.MarkDirty();
    }

    public bool Prepare(
        RenderableMesh renderable,
        bool realtimeSkinned,
        bool forceRebuild = false,
        uint maxLeafPrimitives = DefaultMaxLeafPrimitives)
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

        ConfigureTree(maxLeafPrimitives);
        EnsureTriangleIndexBuffer(mesh, triangles);
        EnsureAabbBuffer(triangleCount);

        bool skinned = realtimeSkinned && mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning;
        if (skinned)
        {
            if (TryUpdateSkinnedAabbs(renderable, renderer, mesh, triangleCount, out XRDataBuffer? skinnedPositions, out XRDataBuffer? skinnedInterleaved))
            {
                LastUpdateUsedGpuSkinning = true;
                LocalToWorldMatrix = renderable.SkinnedBvhLocalToWorldMatrix;
                bool ready = BuildOrRefit(
                    mesh,
                    triangleCount,
                    renderable.RenderInfo.LocalCullingVolume ?? mesh.Bounds,
                    forceRebuild,
                    aabbsUpdated: true);
                return ready && PackTriangles(
                    mesh,
                    triangleCount,
                    skinnedPositions,
                    skinnedInterleaved,
                    applyTransform: true,
                    renderable.SkinnedBvhWorldToLocalMatrix);
            }

            if (IsBvhReady)
                return IsBvhReady;

            // Compute-skinned buffers can be unavailable when compute skinning is disabled
            // or the prepass has not produced output yet. Keep GPU preview visible by
            // building a bind-pose GPU BVH once, then refit it later when skinned buffers appear.
            LastUpdateUsedGpuSkinning = false;
            LocalToWorldMatrix = renderable.Component.Transform.RenderMatrix;
            InvalidateStaticGeometryIfChanged(mesh);
            bool fallbackAabbsWillUpload = !_staticAabbsUploaded;
            if (!UploadStaticAabbsIfNeeded(mesh, triangles, triangleCount))
                return false;

            bool fallbackReady = BuildOrRefit(mesh, triangleCount, mesh.Bounds, forceRebuild, fallbackAabbsWillUpload);
            return fallbackReady && PackStaticTriangles(mesh, triangleCount);
        }

        LastUpdateUsedGpuSkinning = false;
        LocalToWorldMatrix = renderable.Component.Transform.RenderMatrix;
        InvalidateStaticGeometryIfChanged(mesh);
        bool staticAabbsWillUpload = !_staticAabbsUploaded;
        if (!UploadStaticAabbsIfNeeded(mesh, triangles, triangleCount))
            return false;

        bool staticReady = BuildOrRefit(mesh, triangleCount, mesh.Bounds, forceRebuild, staticAabbsWillUpload);
        return staticReady && PackStaticTriangles(mesh, triangleCount);
    }

    private bool ClearReadyState()
    {
        _built = false;
        _triangleCount = 0;
        _packedTrianglesUploaded = false;
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
        _packedTrianglesUploaded = false;
        _staticGeometrySource = null;
        _staticGeometryRevision = 0u;
        _tree.MarkDirty();
    }

    private void InvalidateStaticGeometryIfChanged(XRMesh mesh)
    {
        XRDataBuffer? source = mesh.Interleaved ? mesh.InterleavedVertexBuffer : mesh.PositionsBuffer;
        ulong revision = source?.Revision ?? 0u;
        if (ReferenceEquals(_staticGeometrySource, source) && _staticGeometryRevision == revision)
            return;

        _staticGeometrySource = source;
        _staticGeometryRevision = revision;
        _staticAabbsUploaded = false;
        _packedTrianglesUploaded = false;
        _built = false;
        ReleaseStaticStorageViews();
        _tree.MarkDirty();
    }

    private void ConfigureTree(uint maxLeafPrimitives)
    {
        _tree.BuildMode = BvhBuildMode.MortonOnly;
        _tree.MaxLeafPrimitives = Math.Max(1u, maxLeafPrimitives);
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

    private bool TryUpdateSkinnedAabbs(
        RenderableMesh renderable,
        XRMeshRenderer renderer,
        XRMesh mesh,
        uint triangleCount,
        out XRDataBuffer? positions,
        out XRDataBuffer? interleaved)
    {
        SkinningPrepassDispatcher.Instance.RunForGpuMeshBvh(renderer);
        var resolvedBuffers = SkinningPrepassDispatcher.Instance.GetSkinnedBuffers(renderer);
        positions = resolvedBuffers.positions;
        interleaved = resolvedBuffers.interleaved;
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
        program.Uniform("PositionStrideScalars", positions?.ComponentCount ?? 0u);
        program.Uniform("ApplyTransform", 1);
        program.Uniform("TransformMatrix", renderable.SkinnedBvhWorldToLocalMatrix);
        program.DispatchCompute(
            ComputeGroups(triangleCount, TriangleAabbGroupSize),
            1u,
            1u,
            EMemoryBarrierMask.ShaderStorage);

        return true;
    }

    private bool PackStaticTriangles(XRMesh mesh, uint triangleCount)
    {
        // Static geometry and its Morton permutation remain valid until the
        // mesh BVH is explicitly dirtied or its source changes. Avoid
        // repacking immutable triangles on every preview/pick preparation.
        if (_packedTrianglesUploaded)
            return true;

        XRDataBuffer? positions = mesh.Interleaved
            ? null
            : GetOrCreateStorageView(
                mesh.PositionsBuffer,
                "GpuMeshBvh_Positions_Storage",
                ref _storagePositionSource,
                ref _storagePositionBuffer);
        XRDataBuffer? interleaved = mesh.Interleaved
            ? GetOrCreateStorageView(
                mesh.InterleavedVertexBuffer,
                "GpuMeshBvh_Interleaved_Storage",
                ref _storageInterleavedSource,
                ref _storageInterleavedBuffer)
            : null;
        return PackTriangles(mesh, triangleCount, positions, interleaved, applyTransform: false, Matrix4x4.Identity);
    }

    private static XRDataBuffer? GetOrCreateStorageView(
        XRDataBuffer? source,
        string name,
        ref XRDataBuffer? cachedSource,
        ref XRDataBuffer? cachedView)
    {
        if (source is null)
            return null;

        bool stale = cachedView is null ||
            !ReferenceEquals(cachedSource, source) ||
            cachedView.Length != source.Length ||
            cachedView.ElementCount != source.ElementCount;
        if (!stale)
            return cachedView;

        cachedView?.Destroy();
        cachedView?.Dispose();

        XRDataBuffer view = source.Clone(cloneBuffer: true, target: EBufferTarget.ShaderStorageBuffer);
        view.AttributeName = name;
        view.ShouldMap = false;
        view.DisposeOnPush = false;
        view.Resizable = source.Resizable;
        view.PadEndingToVec4 = source.PadEndingToVec4;
        view.PushData();

        cachedSource = source;
        cachedView = view;
        return view;
    }

    private void ReleaseStaticStorageViews()
    {
        _storagePositionBuffer?.Destroy();
        _storagePositionBuffer?.Dispose();
        _storageInterleavedBuffer?.Destroy();
        _storageInterleavedBuffer?.Dispose();
        _storagePositionSource = null;
        _storagePositionBuffer = null;
        _storageInterleavedSource = null;
        _storageInterleavedBuffer = null;
    }

    private bool PackTriangles(
        XRMesh mesh,
        uint triangleCount,
        XRDataBuffer? positions,
        XRDataBuffer? interleaved,
        bool applyTransform,
        Matrix4x4 transformMatrix)
    {
        XRDataBuffer? source = positions ?? interleaved;
        XRDataBuffer? morton = _tree.MortonBuffer;
        if (source is null || morton is null || _triangleIndexBuffer is null || triangleCount == 0)
            return false;

        EnsurePackedTriangleBuffer(triangleCount);
        if (!EnsurePackedTriangleProgramReady())
            return false;

        XRRenderProgram program = _packedTriangleProgram!;
        program.BindBuffer(positions ?? source, 0);
        program.BindBuffer(interleaved ?? source, 1);
        program.BindBuffer(_triangleIndexBuffer, 2);
        program.BindBuffer(morton, 3);
        program.BindBuffer(_packedTriangleBuffer!, 4);
        program.Uniform("TriangleCount", triangleCount);
        program.Uniform("UseInterleaved", interleaved is not null ? 1u : 0u);
        program.Uniform("InterleavedStrideBytes", mesh.InterleavedStride);
        program.Uniform("PositionOffsetBytes", mesh.PositionOffset);
        program.Uniform("PositionStrideScalars", positions?.ComponentCount ?? 0u);
        program.Uniform("ApplyTransform", applyTransform ? 1 : 0);
        program.Uniform("TransformMatrix", transformMatrix);
        program.DispatchCompute(
            ComputeGroups(triangleCount, PackedTriangleGroupSize),
            1u,
            1u,
            EMemoryBarrierMask.ShaderStorage);

        _packedTrianglesUploaded = true;
        return true;
    }

    private void EnsurePackedTriangleBuffer(uint triangleCount)
    {
        if (_packedTriangleBuffer is null)
        {
            _packedTriangleBuffer = new XRDataBuffer(
                "GpuMeshBvh_PackedTriangles",
                EBufferTarget.ShaderStorageBuffer,
                Math.Max(triangleCount, 1u),
                EComponentType.Struct,
                (uint)Marshal.SizeOf<PackedTriangle>(),
                false,
                true)
            {
                Usage = EBufferUsage.DynamicDraw,
                Resizable = true,
                DisposeOnPush = false,
                PadEndingToVec4 = true,
                ShouldMap = false
            };
            _packedTrianglesUploaded = false;
        }
        else if (_packedTriangleBuffer.ElementCount < triangleCount)
        {
            _packedTriangleBuffer.Resize(triangleCount, false, true);
            _packedTrianglesUploaded = false;
        }
    }

    private bool EnsurePackedTriangleProgramReady()
    {
        if (_packedTriangleProgram is null)
        {
            _packedTriangleShader ??= ShaderHelper.LoadEngineShader(PackedTriangleShaderPath, EShaderType.Compute);
            _packedTriangleProgram = new XRRenderProgram(true, false, _packedTriangleShader);
        }

        if (_packedTriangleProgram.IsLinked)
            return true;

        _packedTriangleProgram.Link();

        return _packedTriangleProgram.IsLinked;
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
        ReleaseStaticStorageViews();
        _aabbBuffer?.Dispose();
        _triangleIndexBuffer?.Dispose();
        _packedTriangleBuffer?.Dispose();
        _triangleAabbProgram?.Destroy();
        _triangleAabbShader?.Destroy();
        _packedTriangleProgram?.Destroy();
        _packedTriangleShader?.Destroy();
        _aabbBuffer = null;
        _triangleIndexBuffer = null;
        _packedTriangleBuffer = null;
        _triangleAabbProgram = null;
        _triangleAabbShader = null;
        _packedTriangleProgram = null;
        _packedTriangleShader = null;
        _staticGeometrySource = null;
        _staticGeometryRevision = 0u;
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PackedTriangle(Vector4 v0, Vector4 v1, Vector4 v2, Vector4 extra)
    {
        public readonly Vector4 V0 = v0;
        public readonly Vector4 V1 = v1;
        public readonly Vector4 V2 = v2;
        public readonly Vector4 Extra = extra;
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// Owns the aggregate, world-space triangle BVH used exclusively by DDGI.
/// It is independent of the draw-submission strategy and never reads geometry
/// or ray results back to the CPU.
/// </summary>
public sealed partial class GpuDdgiGeometryService : IDisposable
{
    private const uint ThreadGroupSize = 128u;
    private const uint CastsShadowsFlag = 1u;
    private readonly GpuBvhTree _tree = new("DDGI.WorldGeometry");
    private readonly Dictionary<RenderableMesh, GpuMeshBvh> _meshSources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RenderableMesh, uint> _objectIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XRMaterial, uint> _materialIndices = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _entries = [];
    private readonly List<TopologySignature> _topology = [];
    private readonly List<TopologySignature> _nextTopology = [];
    private readonly List<DDGIMaterialGpu> _materials = [];
    private readonly List<DDGIMaterialGpu> _uploadedMaterials = [];
    private readonly List<Entry> _previousEntries = [];
    private readonly HashSet<RenderableMesh> _seenMeshes = new(ReferenceEqualityComparer.Instance);
    private readonly List<RenderableMesh> _removedMeshes = [];
    private XRDataBuffer? _aabbs;
    private XRDataBuffer? _triangles;
    private XRDataBuffer? _unsortedTriangles;
    private XRDataBuffer? _materialsBuffer;
    private XRDataBuffer? _attributes;
    private XRDataBuffer? _emptyNodes;
    private XRShader? _aabbShader;
    private XRShader? _packShader;
    private XRRenderProgram? _aabbProgram;
    private XRRenderProgram? _packProgram;
    private XRRenderProgram? _permuteProgram;
    private ulong _preparedFrame = ulong.MaxValue;
    private ulong _submissionFrame = ulong.MaxValue;
    private XRGpuFence? _submissionFence;
    private AABB _normalizationBounds;
    private bool _hasNormalizationBounds;
    private bool _geometryChanged;
    private bool _needsGeometryUpdate = true;
    private uint _nextObjectId = 1u;
    private bool _minimalBuffersUploaded;
    private bool _disposed;

    public XRDataBuffer? Nodes => _tree.NodeBuffer ?? _emptyNodes;
    public XRDataBuffer? Triangles => _triangles;
    public XRDataBuffer? Materials => _materialsBuffer;
    public XRDataBuffer? Attributes => _attributes;
    public uint NodeCount => _tree.NodeCount;
    public uint TriangleCount { get; private set; }
    public uint MaterialCount => (uint)_materials.Count;
    public uint RootIndex => 0u;
    public DDGIGeometryStatus Status { get; private set; } = DDGIGeometryStatus.Unprepared;
    public string? Diagnostic { get; private set; }
    public bool IsReady => Status == DDGIGeometryStatus.Ready && Nodes is not null && Triangles is not null && Materials is not null;

    public GpuDdgiGeometryService()
    {
        _tree.BuildMode = BvhBuildMode.MortonOnly;
        _tree.MaxLeafPrimitives = 1u;
    }

    /// <summary>Prepares or refits world-space DDGI geometry. A false result is retryable unless disposed.</summary>
    public bool Prepare(VisualScene3D scene)
    {
        if (_disposed)
        {
            SetStatus(DDGIGeometryStatus.Disposed, "The DDGI geometry service has been disposed.");
            return false;
        }
        if (scene is null || !RuntimeEngine.IsRenderThread || AbstractRenderer.Current is null)
        {
            SetStatus(DDGIGeometryStatus.PendingRenderThread, "DDGI world geometry must be prepared on the render thread.");
            return false;
        }

        ulong frame = RuntimeEngine.Rendering.State.RenderFrameId;
        if (!ResolveGeometrySubmission(frame))
            return false;
        if (_preparedFrame == frame && IsReady)
            return true;
        EnsureMinimalBuffers();
        _materialTextures.BeginFrame(frame);
        bool topologyChanged;
        AABB sceneBounds;
        try
        {
            if (!CollectEntries(scene, out topologyChanged, out sceneBounds))
                return false;
        }
        catch (InvalidOperationException ex)
        {
            _materialTextures.PruneUnused();
            SetStatus(DDGIGeometryStatus.Failed, ex.Message);
            return false;
        }
        if (!_materialTextures.Prepare(out string? materialDiagnostic))
        {
            _needsGeometryUpdate = true;
            SetStatus(DDGIGeometryStatus.PendingGpuProgramLink, materialDiagnostic);
            return false;
        }

        if (TriangleCount == 0u)
        {
            _tree.Clear();
            UploadMaterials();
            _preparedFrame = frame;
            SetStatus(DDGIGeometryStatus.Ready, "Scene has no DDGI-eligible triangles; trace consumers must emit misses.");
            return true;
        }

        _tree.PollPendingOverflow();
        if (!EnsureProgramsReady() || !_tree.EnsureProgramsReady(TriangleCount))
        {
            _needsGeometryUpdate = true;
            SetStatus(DDGIGeometryStatus.PendingGpuProgramLink, "DDGI geometry and BVH programs are still linking.");
            return false;
        }
        EnsureAggregateBuffers(TriangleCount);
        UploadMaterials();
        bool rebuild = topologyChanged || _tree.IsDirty || _tree.PrimitiveCount != TriangleCount || !_hasNormalizationBounds ||
            Vector3.Min(_normalizationBounds.Min, sceneBounds.Min) != _normalizationBounds.Min ||
            Vector3.Max(_normalizationBounds.Max, sceneBounds.Max) != _normalizationBounds.Max;
        if (!rebuild && !_geometryChanged && !_needsGeometryUpdate)
        {
            _preparedFrame = frame;
            SetStatus(DDGIGeometryStatus.Ready, Diagnostic);
            return true;
        }

        DispatchAabbs();
        if (rebuild)
        {
            _tree.Build(_aabbs!, TriangleCount, sceneBounds);
            _normalizationBounds = sceneBounds;
            _hasNormalizationBounds = true;
        }
        else
            _tree.Refit();

        if (_tree.IsDirty || _tree.IsBuildPendingResources || _tree.NodeBuffer is null || _tree.NodeCount == 0u || _tree.PrimitiveCount != TriangleCount)
        {
            _needsGeometryUpdate = true;
            SetStatus(DDGIGeometryStatus.PendingGpuProgramLink, "DDGI BVH resources or compute programs are still pending.");
            return false;
        }

        DispatchPackedTriangles();
        if (!CaptureGeometrySubmission(frame))
            return false;
        _needsGeometryUpdate = false;
        _preparedFrame = frame;
        SetStatus(DDGIGeometryStatus.Ready, Diagnostic);
        return true;
    }

    private bool CollectEntries(VisualScene3D scene, out bool topologyChanged, out AABB sceneBounds)
    {
        topologyChanged = false;
        Diagnostic = null;
        sceneBounds = default;
        bool hasBounds = false;
        uint triangleOffset = 0u;
        _materials.Clear();
        _materials.Add(DDGIMaterialGpu.Default);
        _materialIndices.Clear();
        _previousEntries.Clear();
        _previousEntries.AddRange(_entries);
        _entries.Clear();
        _seenMeshes.Clear();
        _nextTopology.Clear();
        TriangleCount = 0u;


        IReadOnlyList<Rendering.Info.RenderInfo3D> renderables = scene.Renderables;
        for (int i = 0; i < renderables.Count; i++)
        {
            Rendering.Info.RenderInfo3D info = renderables[i];
            RenderableMesh? renderable = info.OwnerRenderableMesh;
            if (renderable is null || !info.IsVisible || !info.VisibleInLightingProbes || renderable.CurrentLODRenderer is null)
                continue;
            _seenMeshes.Add(renderable);

            if (!_meshSources.TryGetValue(renderable, out GpuMeshBvh? sourceBvh))
            {
                sourceBvh = new GpuMeshBvh();
                _meshSources.Add(renderable, sourceBvh);
                topologyChanged = true;
            }

            bool sourceReady = sourceBvh.TryGetGeometrySources(renderable, out GpuMeshBvhGeometrySources source);
            if (!sourceReady)
            {
                if (!string.IsNullOrEmpty(source.DeformationDiagnostic))
                    SetStatus(DDGIGeometryStatus.Failed, source.DeformationDiagnostic);
                else if (source.IsSkinningPending)
                    SetStatus(DDGIGeometryStatus.PendingSkinning, "A deformed DDGI mesh is waiting for current GPU deformation output.");
                else
                    SetStatus(DDGIGeometryStatus.PendingGpuProgramLink, "A DDGI mesh GPU source is not ready yet.");
                return false;
            }

            ValidateSourceLayout(source);
            XRMaterial? material = renderable.MaterialOverride ?? source.Renderer.Material;
            uint materialIndex = ResolveMaterial(material);
            ValidateMaterialCoordinates(_materials[(int)materialIndex], source.Mesh);
            if (!_objectIds.TryGetValue(renderable, out uint objectId))
            {
                objectId = _nextObjectId++;
                _objectIds.Add(renderable, objectId);
            }

            _entries.Add(new Entry(renderable, source, triangleOffset, materialIndex, info.CastsShadows ? CastsShadowsFlag : 0u, objectId));
            _nextTopology.Add(new TopologySignature(renderable, source.Mesh, source.TriangleCount, source.Mesh.GeometryRevision));
            triangleOffset = checked(triangleOffset + source.TriangleCount);
            TriangleCount = triangleOffset;
            if (TriangleCount > 2_000_000u)
            {
                SetStatus(DDGIGeometryStatus.Failed, "DDGI aggregate geometry exceeds the current 2,000,000-triangle limit.");
                return false;
            }

            if (renderable.TryGetWorldBounds(out AABB bounds) && bounds.IsValid)
            {
                sceneBounds = hasBounds
                    ? new AABB(Vector3.Min(sceneBounds.Min, bounds.Min), Vector3.Max(sceneBounds.Max, bounds.Max))
                    : bounds;
                hasBounds = true;
            }
        }

        if (TriangleCount > 0u && !hasBounds)
            sceneBounds = new AABB(new Vector3(-1.0f), Vector3.One);
        if (_topology.Count != _nextTopology.Count)
        {
            topologyChanged = true;
        }
        else
        {
            for (int i = 0; i < _topology.Count; i++)
            {
                if (_topology[i] == _nextTopology[i])
                    continue;
                topologyChanged = true;
                break;
            }
        }
        _topology.Clear();
        _topology.AddRange(_nextTopology);
        _geometryChanged = _entries.Count != _previousEntries.Count;
        for (int i = 0; i < _entries.Count; i++)
            _geometryChanged |= _entries[i].Source.IsGpuDeformed || i >= _previousEntries.Count || _entries[i] != _previousEntries[i];
        _removedMeshes.Clear();
        foreach (RenderableMesh mesh in _meshSources.Keys)
            if (!_seenMeshes.Contains(mesh))
                _removedMeshes.Add(mesh);
        for (int i = 0; i < _removedMeshes.Count; i++)
        {
            RenderableMesh mesh = _removedMeshes[i];
            _meshSources[mesh].Dispose();
            _meshSources.Remove(mesh);
            _objectIds.Remove(mesh);
        }
        return true;
    }

    private void EnsureMinimalBuffers()
    {
        _emptyNodes ??= CreateBuffer("DDGI.EmptyNodes", 4u, EComponentType.UInt, 1u);
        if (!_minimalBuffersUploaded)
        {
            _emptyNodes.SetDataRaw(new uint[] { 0u, 0u, 0u, 1u });
            _emptyNodes.PushData();
            _minimalBuffersUploaded = true;
        }
        _triangles ??= CreateBuffer("DDGI.WorldTriangles", 1u, EComponentType.Struct, (uint)Marshal.SizeOf<PackedTriangleGpu>());
        _materialsBuffer ??= CreateBuffer("DDGI.WorldMaterials", 1u, EComponentType.Struct, (uint)Marshal.SizeOf<DDGIMaterialGpu>());
        _attributes ??= CreateBuffer("DDGI.TriangleAttributes", 1u, EComponentType.Struct, (uint)Marshal.SizeOf<DDGITriangleAttributesGpu>());
    }

    private void EnsureAggregateBuffers(uint count)
    {
        uint capacity = Math.Max(count, 1u);
        _aabbs ??= CreateBuffer("DDGI.WorldAabbs", capacity, EComponentType.Struct, (uint)Marshal.SizeOf<TriangleAabbGpu>());
        _unsortedTriangles ??= CreateBuffer("DDGI.UnsortedTriangles", capacity, EComponentType.Struct, (uint)Marshal.SizeOf<PackedTriangleGpu>());
        _triangles ??= CreateBuffer("DDGI.WorldTriangles", capacity, EComponentType.Struct, (uint)Marshal.SizeOf<PackedTriangleGpu>());
        if (_aabbs.ElementCount < capacity)
            _aabbs.Resize(capacity, false, true);
        if (_triangles.ElementCount < capacity)
            _triangles.Resize(capacity, false, true);
        if (_unsortedTriangles.ElementCount < capacity)
            _unsortedTriangles.Resize(capacity, false, true);
        if (_attributes!.ElementCount < capacity)
            _attributes.Resize(capacity, false, true);
    }

    private void UploadMaterials()
    {
        if (MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_materials)).SequenceEqual(
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_uploadedMaterials))))
            return;
        uint count = (uint)Math.Max(_materials.Count, 1);
        if (_materialsBuffer is null)
            _materialsBuffer = CreateBuffer("DDGI.WorldMaterials", count, EComponentType.Struct, (uint)Marshal.SizeOf<DDGIMaterialGpu>());
        else if (_materialsBuffer.ElementCount < count)
            _materialsBuffer.Resize(count, false, true);
        _materialsBuffer.SetDataRaw(CollectionsMarshal.AsSpan(_materials));
        _materialsBuffer.PushData();
        _uploadedMaterials.Clear();
        _uploadedMaterials.AddRange(_materials);
    }

    private bool EnsureProgramsReady()
    {
        _aabbProgram ??= new XRRenderProgram(true, false, _aabbShader ??= ShaderHelper.LoadEngineShader("Compute/DDGIGeometry/ddgi_geometry_aabbs.comp", EShaderType.Compute)) { Name = "DDGI.Geometry.Aabbs" };
        _packProgram ??= new XRRenderProgram(true, false, _packShader ??= ShaderHelper.LoadEngineShader("Compute/DDGIGeometry/ddgi_geometry_pack.comp", EShaderType.Compute)) { Name = "DDGI.Geometry.Pack" };
        _permuteProgram ??= new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/DDGIGeometry/ddgi_geometry_permute.comp", EShaderType.Compute)) { Name = "DDGI.Geometry.Permute" };
        if (!_aabbProgram.IsLinked)
            _aabbProgram.Link();
        if (!_packProgram.IsLinked)
            _packProgram.Link();
        if (!_permuteProgram.IsLinked)
            _permuteProgram.Link();
        if (_aabbProgram.IsLinked && _packProgram.IsLinked && _permuteProgram.IsLinked)
            return true;
        SetStatus(DDGIGeometryStatus.PendingGpuProgramLink, "DDGI aggregate geometry compute programs have not linked yet.");
        return false;
    }

    private void DispatchAabbs()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            BindEntry(_aabbProgram!, entry);
            _aabbProgram!.BindBuffer(_aabbs!, 3);
            _aabbProgram.Uniform("uEntryTriangleCount", entry.Source.TriangleCount);
            _aabbProgram.Uniform("uAggregateOffset", entry.Offset);
            _aabbProgram.DispatchCompute(ComputeGroups(entry.Source.TriangleCount), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }
    }

    private void DispatchPackedTriangles()
    {
        XRDataBuffer? morton = _tree.MortonBuffer;
        if (morton is null)
            return;
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            BindEntry(_packProgram!, entry);
            BindMaterialAttributes(_packProgram!, entry.Source);
            _packProgram!.BindBuffer(_attributes!, 3);
            _packProgram!.BindBuffer(_unsortedTriangles!, 4);
            _packProgram.Uniform("uAggregateTriangleCount", TriangleCount);
            _packProgram.Uniform("uEntryTriangleCount", entry.Source.TriangleCount);
            _packProgram.Uniform("uAggregateOffset", entry.Offset);
            _packProgram.Uniform("uMaterialIndex", entry.MaterialIndex);
            _packProgram.Uniform("uTriangleFlags", entry.Flags);
            _packProgram.Uniform("uObjectId", entry.ObjectId);
            _packProgram.DispatchCompute(ComputeGroups(entry.Source.TriangleCount), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
        }
        _permuteProgram!.BindBuffer(_unsortedTriangles!, 0);
        _permuteProgram.BindBuffer(morton, 1);
        _permuteProgram.BindBuffer(_triangles!, 2);
        _permuteProgram.Uniform("uTriangleCount", TriangleCount);
        _permuteProgram.DispatchCompute(ComputeGroups(TriangleCount), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
    }

    private static void BindEntry(XRRenderProgram program, in Entry entry)
    {
        GpuMeshBvhGeometrySources source = entry.Source;
        XRDataBuffer sourceBuffer = source.Positions ?? source.Interleaved!;
        program.BindBuffer(source.Positions ?? sourceBuffer, 0);
        program.BindBuffer(source.Interleaved ?? sourceBuffer, 1);
        program.BindBuffer(source.TriangleIndices, 2);
        program.Uniform("uUseInterleaved", source.UseInterleaved ? 1u : 0u);
        program.Uniform("uInterleavedStrideBytes", source.InterleavedStrideBytes);
        program.Uniform("uPositionOffsetBytes", source.PositionOffsetBytes);
        program.Uniform("uPositionStrideScalars", source.PositionStrideScalars);
        program.Uniform("uLocalToWorld", source.LocalToWorld);
    }

    private static void BindMaterialAttributes(XRRenderProgram program, in GpuMeshBvhGeometrySources source)
    {
        XRMesh mesh = source.Mesh;
        XRDataBuffer fallback = source.Positions ?? source.Interleaved!;
        program.BindBuffer(source.Normals ?? fallback, 5);
        program.BindBuffer(source.TexCoords0 ?? fallback, 6);
        program.BindBuffer(source.TexCoords1 ?? fallback, 7);
        program.Uniform("uHasNormals", mesh.HasNormals && (source.Normals is not null || source.UseInterleaved) ? 1u : 0u);
        program.Uniform("uNormalsInterleaved", source.Normals is null && source.UseInterleaved ? 1u : 0u);
        program.Uniform("uNormalOffsetBytes", mesh.NormalOffset ?? 0u);
        program.Uniform("uNormalStrideScalars", source.Normals?.ComponentCount ?? 3u);
        program.Uniform("uTexCoordsInterleaved", source.TexCoordsInterleaved ? 1u : 0u);
        program.Uniform("uTexCoordCount", mesh.TexCoordCount);
        program.Uniform("uTexCoordOffsetBytes", mesh.TexCoordOffset ?? 0u);
        program.Uniform("uUv0StrideScalars", source.TexCoords0?.ComponentCount ?? 2u);
        program.Uniform("uUv1StrideScalars", source.TexCoords1?.ComponentCount ?? 2u);
        Matrix4x4 normalMatrix = Matrix4x4.Invert(source.LocalToWorld, out Matrix4x4 inverse)
            ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;
        program.Uniform("uNormalToWorld", normalMatrix);
    }

    private static XRDataBuffer CreateBuffer(string name, uint elements, EComponentType type, uint componentCount)
        => new(name, EBufferTarget.ShaderStorageBuffer, Math.Max(elements, 1u), type, componentCount, false, true)
        {
            Usage = EBufferUsage.DynamicDraw,
            Resizable = true,
            DisposeOnPush = false,
            PadEndingToVec4 = true,
            ShouldMap = false,
        };

    private static uint ComputeGroups(uint count) => Math.Max(1u, (count + ThreadGroupSize - 1u) / ThreadGroupSize);
    private void SetStatus(DDGIGeometryStatus status, string? diagnostic) { Status = status; Diagnostic = diagnostic; }

    /// <summary>
    /// Resolves the backend receipt for the most recently authored aggregate
    /// geometry. A Vulkan frame can reject its queued dispatches after this
    /// service has returned to same-frame consumers, so cache cleanliness is
    /// only retained once the backend accepts that command stream.
    /// </summary>
    private bool ResolveGeometrySubmission(ulong frame)
    {
        if (_submissionFence is null)
            return true;

        switch (_submissionFence.SubmissionStatus)
        {
            case EGpuFenceSubmissionStatus.AwaitingSubmission when _submissionFrame == frame:
                // The ordered dispatches and their consumers share this frame's
                // command stream. They may use the queued data without waiting.
                return true;
            case EGpuFenceSubmissionStatus.AwaitingSubmission:
                SetStatus(DDGIGeometryStatus.PendingGpuProgramLink,
                    "DDGI aggregate geometry is waiting for backend submission acceptance.");
                return false;
            case EGpuFenceSubmissionStatus.Submitted when _submissionFence.Poll() != EGpuFenceStatus.Failed:
                ReleaseSubmissionFence();
                return true;
            default:
                ReleaseSubmissionFence();
                InvalidateRejectedGeometry();
                return true;
        }
    }

    private bool CaptureGeometrySubmission(ulong frame)
    {
        XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();
        if (fence is null)
        {
            InvalidateRejectedGeometry();
            SetStatus(DDGIGeometryStatus.PendingGpuProgramLink,
                "DDGI aggregate geometry could not acquire a backend submission receipt.");
            return false;
        }

        ReleaseSubmissionFence();
        _submissionFence = fence;
        _submissionFrame = frame;
        return true;
    }

    private void InvalidateRejectedGeometry()
    {
        _preparedFrame = ulong.MaxValue;
        _hasNormalizationBounds = false;
        _geometryChanged = true;
        _needsGeometryUpdate = true;
        _tree.MarkDirty();
    }

    private void ReleaseSubmissionFence()
    {
        _submissionFence?.Dispose();
        _submissionFence = null;
        _submissionFrame = ulong.MaxValue;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseSubmissionFence();
        foreach (GpuMeshBvh source in _meshSources.Values)
            source.Dispose();
        _tree.Dispose();
        _materialTextures.Dispose();
        _aabbs?.Dispose();
        _triangles?.Dispose();
        _unsortedTriangles?.Dispose();
        _materialsBuffer?.Dispose();
        _attributes?.Dispose();
        _emptyNodes?.Dispose();
        _aabbProgram?.Destroy();
        _packProgram?.Destroy();
        _permuteProgram?.Destroy();
        // ShaderHelper owns these shared shader assets; only the programs and
        // generated resources belong to this geometry service.
        _aabbShader = null;
        _packShader = null;
        SetStatus(DDGIGeometryStatus.Disposed, "The DDGI geometry service has been disposed.");
    }

    private readonly record struct Entry(RenderableMesh Renderable, GpuMeshBvhGeometrySources Source, uint Offset, uint MaterialIndex, uint Flags, uint ObjectId);
    private readonly record struct TopologySignature(RenderableMesh Renderable, XRMesh Mesh, uint TriangleCount, long GeometryRevision);
    [StructLayout(LayoutKind.Sequential)] private readonly struct TriangleAabbGpu(Vector4 min, Vector4 max) { public readonly Vector4 Min = min; public readonly Vector4 Max = max; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct PackedTriangleGpu(Vector4 v0, Vector4 v1, Vector4 v2, Vector4 extra) { public readonly Vector4 V0 = v0; public readonly Vector4 V1 = v1; public readonly Vector4 V2 = v2; public readonly Vector4 Extra = extra; }
}

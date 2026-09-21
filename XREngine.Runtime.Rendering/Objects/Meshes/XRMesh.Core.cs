using XREngine.Extensions;
using MemoryPack;
using SimpleScene.Util.ssBVH;
using System;
using System.ComponentModel;
using System.Numerics;
using System.Threading;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace XREngine.Rendering;

public enum ESkinningShaderConvention : byte
{
    LegacyImplicitTranspose = 0,
    ExplicitRowMajorRowVector = 1,
}

public enum SkinningInfluenceEncoding : byte
{
    None = 0,
    Core4Spill = 1,
    Core4NoSpill = 2,
}

public enum SkinningCoreIndexFormat : byte
{
    None = 0,
    Core4x8 = 1,
    Core4x16 = 2,
}

[Flags]
public enum BlendshapeShaderVariant : byte
{
    None = 0,
    ActiveList = 1 << 0,
    SparseDeltas = 1 << 1,
    QuantizedDeltas = 1 << 2,
    PrecombinedDeltas = 1 << 3,
    LodTier = 1 << 4,
    BasisCompression = 1 << 5,
}

public enum BlendshapeDeltaStorageMode : byte
{
    DensePerVertex = 0,
    SparseAugmentsDenseFallback = 1,
    SparsePreferred = 2,
}

public enum BlendshapeDeltaEncoding : byte
{
    Float32 = 0,
    Snorm16Vector3 = 1,
    Snorm16PositionSnorm8NormalTangent = Snorm16Vector3,
}

[XRAssetInspector("XREngine.Editor.AssetEditors.XRMeshInspector")]
[MemoryPackable(GenerateType.NoGenerate)]
public partial class XRMesh : XRAsset
{
    private delegate void DelVertexAction(XRMesh @this, int index, int remappedIndex, Vertex vtx, Matrix4x4? dataTransform);

    [MemoryPackIgnore]
    [YamlIgnore] public XREvent<XRMesh>? DataChanged;

    // Interleaving / layout
    private bool _interleaved;
    public bool Interleaved { get => _interleaved; set => SetField(ref _interleaved, value); }

    private uint _interleavedStride;
    public uint InterleavedStride { get => _interleavedStride; set => SetField(ref _interleavedStride, value); }

    private uint _positionOffset;
    public uint PositionOffset { get => _positionOffset; set => SetField(ref _positionOffset, value); }

    private uint? _normalOffset;
    public uint? NormalOffset { get => _normalOffset; set => SetField(ref _normalOffset, value); }

    private uint? _tangentOffset;
    public uint? TangentOffset { get => _tangentOffset; set => SetField(ref _tangentOffset, value); }

    private uint? _colorOffset;
    public uint? ColorOffset { get => _colorOffset; set => SetField(ref _colorOffset, value); }

    private uint? _texCoordOffset;
    public uint? TexCoordOffset { get => _texCoordOffset; set => SetField(ref _texCoordOffset, value); }

    private uint _colorCount;
    public uint ColorCount { get => _colorCount; set => SetField(ref _colorCount, value); }

    private uint _texCoordCount;
    public uint TexCoordCount { get => _texCoordCount; set => SetField(ref _texCoordCount, value); }

    private int _vertexCount;
    public int VertexCount
    {
        get => _vertexCount;
        internal set
        {
            if (!SetField(ref _vertexCount, value))
                return;
            AdvanceGeometryRevision();
        }
    }

    private long _geometryRevision = 1;
    /// <summary>Monotonically advances whenever mesh geometry or topology changes.</summary>
    [Browsable(false)]
    [MemoryPackIgnore]
    [YamlIgnore]
    public long GeometryRevision => Interlocked.Read(ref _geometryRevision);

    [YamlIgnore]
    private Vertex[] _vertices = [];
    [Browsable(false)]
    [YamlIgnore]
    public Vertex[] Vertices
    {
        get => _vertices;
        private set
        {
            SetField(ref _vertices, value);
            AdvanceGeometryRevision();
        }
    }

    // Primitive index storage
    private List<int>? _points;
    private List<IndexLine>? _lines;
    private List<IndexTriangle>? _triangles;
    [MemoryPackIgnore]
    [YamlIgnore]
    internal Dictionary<Triangle, (IndexTriangle Indices, int FaceIndex)>? TriangleLookup { get; set; }
    private EPrimitiveType _type = EPrimitiveType.Triangles;

    [Browsable(false)]
    public List<int>? Points
    {
        get => _points;
        set
        {
            InvalidateIndexBufferCache(EPrimitiveType.Points);
            InvalidateIndexBufferCache(EPrimitiveType.Patches);
            SetField(ref _points, value);
            AdvanceGeometryRevision();
        }
    }

    [Browsable(false)]
    public List<IndexLine>? Lines
    {
        get => _lines;
        set
        {
            InvalidateIndexBufferCache(EPrimitiveType.Lines);
            InvalidateIndexBufferCache(EPrimitiveType.Patches);
            SetField(ref _lines, value);
            AdvanceGeometryRevision();
        }
    }

    [Browsable(false)]
    [YamlIgnore]
    public List<IndexTriangle>? Triangles
    {
        get => _triangles;
        set
        {
            InvalidateIndexBufferCache(EPrimitiveType.Triangles);
            InvalidateIndexBufferCache(EPrimitiveType.Patches);
            SetField(ref _triangles, value);
            AdvanceGeometryRevision();
        }
    }

    [Browsable(false)]
    public EPrimitiveType Type
    {
        get => _type;
        set
        {
            SetField(ref _type, value);
            AdvanceGeometryRevision();
        }
    }

    private int _patchVertices = 3;
    public int PatchVertices
    {
        get => _patchVertices;
        set
        {
            int normalized = value < 1 ? 1 : value;
            InvalidateIndexBufferCache(EPrimitiveType.Patches);
            SetField(ref _patchVertices, normalized);
            AdvanceGeometryRevision();
        }
    }

    private AABB _bounds = new(Vector3.Zero, Vector3.Zero);
    public AABB Bounds
    {
        get => _bounds;
        private set => SetField(ref _bounds, value);
    }

    // Bone usage / skinning
    [MemoryPackIgnore]
    private (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] _utilizedBones = [];
    public (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] UtilizedBones
    {
        get => _utilizedBones;
        set => SetField(ref _utilizedBones, value);
    }

    [MemoryPackIgnore]
    [YamlIgnore]
    private IReadOnlyDictionary<TransformBase, TransformBase>? _runtimeBoneReferenceRemap;
    [MemoryPackIgnore]
    [YamlIgnore]
    internal IReadOnlyDictionary<TransformBase, TransformBase>? RuntimeBoneReferenceRemap
    {
        get => _runtimeBoneReferenceRemap;
        private set => SetField(ref _runtimeBoneReferenceRemap, value);
    }

    private ESkinningShaderConvention _skinningShaderConvention = ESkinningShaderConvention.LegacyImplicitTranspose;
    public ESkinningShaderConvention SkinningShaderConvention
    {
        get => _skinningShaderConvention;
        set => SetField(ref _skinningShaderConvention, value);
    }

    private SkinningInfluenceEncoding _skinningInfluenceEncoding = SkinningInfluenceEncoding.None;
    public SkinningInfluenceEncoding SkinningInfluenceEncoding
    {
        get => _skinningInfluenceEncoding;
        private set => SetField(ref _skinningInfluenceEncoding, value);
    }

    private SkinningCoreIndexFormat _skinningCoreIndexFormat = SkinningCoreIndexFormat.None;
    public SkinningCoreIndexFormat SkinningCoreIndexFormat
    {
        get => _skinningCoreIndexFormat;
        private set => SetField(ref _skinningCoreIndexFormat, value);
    }

    private bool _hasSpillInfluences;
    public bool HasSpillInfluences
    {
        get => _hasSpillInfluences;
        private set => SetField(ref _hasSpillInfluences, value);
    }

    private int _maxSpillInfluenceCount;
    public int MaxSpillInfluenceCount
    {
        get => _maxSpillInfluenceCount;
        private set => SetField(ref _maxSpillInfluenceCount, value);
    }

    /// <summary>
    /// The import-root's BindMatrix at the time this mesh was created.
    /// Used by the renderer to convert InverseBindMatrices from world-space to
    /// root-local-space so they match the vertex coordinate frame produced by geometryTransform.
    /// Null means the root was at the world origin (Identity).
    /// </summary>
    [MemoryPackIgnore]
    public Matrix4x4? BindRootMatrix { get; set; }

    public bool HasSkinning => _utilizedBones is { Length: > 0 };
    public bool IsSingleBound => UtilizedBones.Length == 1;
    public bool IsUnskinned => UtilizedBones.Length == 0;
    public bool SupportsComputeSkinning => HasCanonicalComputeSkinningBuffers();

    // Blendshapes
    private string[] _blendshapeNames = [];
    [Browsable(false)]
    public string[] BlendshapeNames
    {
        get => _blendshapeNames;
        set => SetField(ref _blendshapeNames, value);
    }
    [MemoryPackIgnore]
    private readonly Dictionary<string, int> _blendshapeNameToIndex = [];
    [Browsable(false)]
    public uint BlendshapeCount => (uint)(BlendshapeNames?.Length ?? 0);
    [Browsable(false)]
    public bool HasBlendshapes => BlendshapeCount > 0;

    private BlendshapeShaderVariant _blendshapeShaderVariant = BlendshapeShaderVariant.None;
    public BlendshapeShaderVariant BlendshapeShaderVariant
    {
        get => _blendshapeShaderVariant;
        private set => SetField(ref _blendshapeShaderVariant, value);
    }

    [MemoryPackIgnore]
    [Browsable(false)]
    public bool HasBlendshapeBasisCompressionPayload
        => (BlendshapeShaderVariant & BlendshapeShaderVariant.BasisCompression) != 0;

    private BlendshapeDeltaStorageMode _blendshapeDeltaStorageMode = BlendshapeDeltaStorageMode.DensePerVertex;
    public BlendshapeDeltaStorageMode BlendshapeDeltaStorageMode
    {
        get => _blendshapeDeltaStorageMode;
        private set => SetField(ref _blendshapeDeltaStorageMode, value);
    }

    private BlendshapeDeltaEncoding _blendshapeDeltaEncoding = BlendshapeDeltaEncoding.Float32;
    public BlendshapeDeltaEncoding BlendshapeDeltaEncoding
    {
        get => _blendshapeDeltaEncoding;
        private set => SetField(ref _blendshapeDeltaEncoding, value);
    }

    private int _blendshapeAffectedVertexCount;
    public int BlendshapeAffectedVertexCount
    {
        get => _blendshapeAffectedVertexCount;
        private set => SetField(ref _blendshapeAffectedVertexCount, value);
    }

    private int _blendshapeSparseRecordCount;
    public int BlendshapeSparseRecordCount
    {
        get => _blendshapeSparseRecordCount;
        private set => SetField(ref _blendshapeSparseRecordCount, value);
    }

    // Buffers (per-vertex)
    [MemoryPackIgnore]
    public XRDataBuffer? PositionsBuffer { get; internal set; }
    [MemoryPackIgnore]
    public XRDataBuffer? NormalsBuffer { get; internal set; }
    [MemoryPackIgnore]
    public XRDataBuffer? TangentsBuffer { get; internal set; }
    [MemoryPackIgnore]
    public XRDataBuffer?[]? ColorBuffers { get; internal set; } = [];
    [MemoryPackIgnore]
    public XRDataBuffer?[]? TexCoordBuffers { get; internal set; } = [];
    [MemoryPackIgnore]
    public XRDataBuffer? InterleavedVertexBuffer { get; private set; }

    // Bone influence buffers. The state references are published atomically so a renderer
    // never combines buffers from different preparation generations.
    private XRMeshSkinningBufferState _skinningBufferState = new(
        null, null, null, null, [], ESkinningShaderConvention.ExplicitRowMajorRowVector,
        SkinningInfluenceEncoding.None, SkinningCoreIndexFormat.None, false, 0, 0);
    private XRMeshBlendshapeBufferState _blendshapeBufferState = new(
        null, null, null, null, null, null, null, BlendshapeShaderVariant.None,
        BlendshapeDeltaStorageMode.DensePerVertex, BlendshapeDeltaEncoding.Float32, 0, 0);
    private readonly Lock _skinningBufferPreparationLock = new();
    private readonly Lock _blendshapeBufferPreparationLock = new();

    // Bone influence buffers
    [MemoryPackIgnore]
    public XRDataBuffer? BoneInfluenceCoreIndices
    {
        get => Volatile.Read(ref _skinningBufferState).CoreIndices;
        private set => Volatile.Write(ref _skinningBufferState, Volatile.Read(ref _skinningBufferState) with { CoreIndices = value });
    }
    [MemoryPackIgnore]
    public XRDataBuffer? BoneInfluenceCoreWeights
    {
        get => Volatile.Read(ref _skinningBufferState).CoreWeights;
        private set => Volatile.Write(ref _skinningBufferState, Volatile.Read(ref _skinningBufferState) with { CoreWeights = value });
    }
    [MemoryPackIgnore]
    public XRDataBuffer? BoneInfluenceSpillHeaders
    {
        get => Volatile.Read(ref _skinningBufferState).SpillHeaders;
        private set => Volatile.Write(ref _skinningBufferState, Volatile.Read(ref _skinningBufferState) with { SpillHeaders = value });
    }
    [MemoryPackIgnore]
    public XRDataBuffer? BoneInfluenceSpillEntries
    {
        get => Volatile.Read(ref _skinningBufferState).SpillEntries;
        private set => Volatile.Write(ref _skinningBufferState, Volatile.Read(ref _skinningBufferState) with { SpillEntries = value });
    }

    // Blendshape indirection
    [MemoryPackIgnore]
    public XRDataBuffer? BlendshapeCounts
    {
        get => Volatile.Read(ref _blendshapeBufferState).Counts;
        private set => Volatile.Write(ref _blendshapeBufferState, Volatile.Read(ref _blendshapeBufferState) with { Counts = value });
    }

    // Non-per-vertex (skinning / blendshape)
    [MemoryPackIgnore]
    public XRDataBuffer? BlendshapeDeltas
    {
        get => Volatile.Read(ref _blendshapeBufferState).Deltas;
        private set => Volatile.Write(ref _blendshapeBufferState, Volatile.Read(ref _blendshapeBufferState) with { Deltas = value });
    }
    [MemoryPackIgnore]
    public XRDataBuffer? BlendshapeIndices
    {
        get => Volatile.Read(ref _blendshapeBufferState).Indices;
        private set => Volatile.Write(ref _blendshapeBufferState, Volatile.Read(ref _blendshapeBufferState) with { Indices = value });
    }
    [MemoryPackIgnore]
    public XRDataBuffer? BlendshapeSparseShapeRanges
    {
        get => Volatile.Read(ref _blendshapeBufferState).SparseShapeRanges;
        private set => Volatile.Write(ref _blendshapeBufferState, Volatile.Read(ref _blendshapeBufferState) with { SparseShapeRanges = value });
    }
    [MemoryPackIgnore]
    public XRDataBuffer? BlendshapeSparseRecords
    {
        get => Volatile.Read(ref _blendshapeBufferState).SparseRecords;
        private set => Volatile.Write(ref _blendshapeBufferState, Volatile.Read(ref _blendshapeBufferState) with { SparseRecords = value });
    }
    [MemoryPackIgnore]
    public XRDataBuffer? BlendshapeQuantizedDeltas
    {
        get => Volatile.Read(ref _blendshapeBufferState).QuantizedDeltas;
        private set => Volatile.Write(ref _blendshapeBufferState, Volatile.Read(ref _blendshapeBufferState) with { QuantizedDeltas = value });
    }
    [MemoryPackIgnore]
    public XRDataBuffer? BlendshapeQuantizationMetadata
    {
        get => Volatile.Read(ref _blendshapeBufferState).QuantizationMetadata;
        private set => Volatile.Write(ref _blendshapeBufferState, Volatile.Read(ref _blendshapeBufferState) with { QuantizationMetadata = value });
    }

    [MemoryPackIgnore]
    private BufferCollection _buffers = [];

    [MemoryPackIgnore]
    public BufferCollection Buffers
    {
        get => _buffers;
        internal set
        {
            BufferCollection next = value ?? [];
            if (ReferenceEquals(_buffers, next))
                return;

            DetachGeometryBufferRevisionTracking(_buffers);
            SetField(ref _buffers, next);
            AttachGeometryBufferRevisionTracking(_buffers);
            OnBuffersAssigned();
            AdvanceGeometryRevision();
        }
    }

    // Weight stats
    private int _maxWeightCount;
    public int MaxWeightCount => _maxWeightCount;

    // BVH / spatial
    [MemoryPackIgnore]
    private BVH<XREngine.Data.Geometry.Triangle>? _bvhTree;
    [MemoryPackIgnore]
    private int _generatingBvh;

    // SDF
    [MemoryPackIgnore]
    public XRTexture3D? SignedDistanceField { get; internal set; }

    [MemoryPackIgnore]
    private readonly Lock _boundsLock = new();

    public XRMesh()
        : base(deferObjectCachePublication: true)
    {
        try
        {
            using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
            JoinDeferredObjectCachePublication();
            AttachGeometryBufferRevisionTracking(_buffers);
            publication.Complete();
        }
        catch
        {
            AbortMeshConstruction();
            throw;
        }
    }

    private XRMesh(bool deferObjectCachePublication)
        : base(deferObjectCachePublication)
        => AttachGeometryBufferRevisionTracking(_buffers);

    internal static XRMesh CreateDeferredForDeserialization()
        => new(deferObjectCachePublication: true);

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        if (propName == nameof(BlendshapeNames) && field is string[] names)
        {
            _blendshapeNameToIndex.Clear();
            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(names[i]) && !_blendshapeNameToIndex.ContainsKey(names[i]))
                    _blendshapeNameToIndex.Add(names[i], i);
                else
                    XREngine.Debug.MeshesWarning($"Duplicate or empty blendshape name '{names[i]}' found in mesh {Name}");
            }
        }
    }

    public override void Destroy(bool now = false)
    {
        if (!now || IsDestroyed)
        {
            base.Destroy(now);
            return;
        }

        BufferCollection buffers = Buffers;
        if (buffers.IsPublicationLeaseHeldByCurrentThread)
        {
            // A synchronous collection observer requested teardown from inside
            // publication. Queue it so the transaction can restore its prior
            // generation before terminal destruction runs.
            base.Destroy(now: false);
            return;
        }

        buffers.BeginOwnerDestruction(this);
        try
        {
            base.Destroy(now: true);
        }
        finally
        {
            // Destroying observers can veto teardown.
            if (!IsDestroyed)
                buffers.CancelOwnerDestruction(this);
        }
    }

    protected override void OnDestroying()
    {
        InvalidateIndexBufferCache();
        // Retirement takes the same gate held from staged swap through root
        // publication, so teardown cannot split an apply/rollback transaction.
        BufferCollection buffers = Buffers;
        buffers.RetireAndDisposeOwnedBuffers();
        DetachGeometryBufferRevisionTracking(buffers);
        base.OnDestroying();
    }

    private void OnBuffersAssigned()
    {
        // After YAML deserialization, we want to ensure the convenience buffer references
        // are hydrated from the serialized buffer collection.
        PositionsBuffer = Buffers.GetValueOrDefault(ECommonBufferType.Position.ToString());
        NormalsBuffer = Buffers.GetValueOrDefault(ECommonBufferType.Normal.ToString());
        TangentsBuffer = Buffers.GetValueOrDefault(ECommonBufferType.Tangent.ToString());
        InterleavedVertexBuffer = Buffers.GetValueOrDefault(ECommonBufferType.InterleavedVertex.ToString());

        if (ColorCount > 0)
        {
            ColorBuffers = new XRDataBuffer[ColorCount];
            for (int i = 0; i < ColorBuffers.Length; i++)
                ColorBuffers[i] = Buffers.GetValueOrDefault($"{ECommonBufferType.Color}{i}");
        }
        else
        {
            ColorBuffers = [];
        }

        if (TexCoordCount > 0)
        {
            TexCoordBuffers = new XRDataBuffer[TexCoordCount];
            for (int i = 0; i < TexCoordBuffers.Length; i++)
                TexCoordBuffers[i] = Buffers.GetValueOrDefault($"{ECommonBufferType.TexCoord}{i}");
        }
        else
        {
            TexCoordBuffers = [];
        }

        if (HasSkinning)
        {
            XRMeshSkinningBufferState state = CaptureSkinningBufferState() with
            {
                CoreIndices = Buffers.GetValueOrDefault(ECommonBufferType.BoneInfluenceCoreIndices.ToString()),
                CoreWeights = Buffers.GetValueOrDefault(ECommonBufferType.BoneInfluenceCoreWeights.ToString()),
                SpillHeaders = Buffers.GetValueOrDefault(ECommonBufferType.BoneInfluenceSpillHeaders.ToString()),
                SpillEntries = Buffers.GetValueOrDefault(ECommonBufferType.BoneInfluenceSpillEntries.ToString()),
            };
            ApplySkinningBufferState(state);
        }

        if (HasBlendshapes)
        {
            XRMeshBlendshapeBufferState state = CaptureBlendshapeBufferState() with
            {
                Counts = Buffers.GetValueOrDefault(ECommonBufferType.BlendshapeCount.ToString()),
                Indices = Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeIndices}Buffer"),
                Deltas = Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeDeltas}Buffer"),
                SparseShapeRanges = Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeSparseShapeRanges}Buffer"),
                SparseRecords = Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeSparseRecords}Buffer"),
                QuantizedDeltas = Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeQuantizedDeltas}Buffer"),
                QuantizationMetadata = Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeQuantizationMetadata}Buffer"),
            };
            ApplyBlendshapeBufferState(state);
        }

        // Rebuild Vertices from buffers if they weren't loaded (we omit them from YAML to reduce file size).
        if ((_vertices is null || _vertices.Length == 0 || _vertices.Length != VertexCount) && VertexCount > 0)
        {
            if (Interleaved)
            {
                if (InterleavedVertexBuffer?.ClientSideSource is null)
                    return;
            }
            else
            {
                if (PositionsBuffer?.ClientSideSource is null)
                    return;
            }

            Vertex[] rebuilt = new Vertex[VertexCount];
            for (uint i = 0; i < (uint)VertexCount; i++)
            {
                Vertex v = new()
                {
                    Position = GetPosition(i),
                };

                if (HasNormals)
                    v.Normal = GetNormal(i);
                if (HasTangents)
                {
                    Vector4 tanSign = GetTangentWithSign(i);
                    v.Tangent = new Vector3(tanSign.X, tanSign.Y, tanSign.Z);
                    v.BitangentSign = tanSign.W;
                }

                if (TexCoordCount > 0)
                {
                    v.TextureCoordinateSets = new List<Vector2>((int)TexCoordCount);
                    for (uint t = 0; t < TexCoordCount; t++)
                        v.TextureCoordinateSets.Add(GetTexCoord(i, t));
                }

                if (ColorCount > 0)
                {
                    v.ColorSets = new List<Vector4>((int)ColorCount);
                    for (uint c = 0; c < ColorCount; c++)
                        v.ColorSets.Add(GetColor(i, c));
                }

                rebuilt[i] = v;
            }

            _vertices = rebuilt;
        }

        // Rebuild Triangles from vertex order if they weren't loaded (we omit them from YAML to reduce file size).
        // This assumes the mesh is stored as a de-indexed triangle list (3 vertices per triangle, sequential indices).
        if (_type == EPrimitiveType.Triangles && (_triangles is null || _triangles.Count == 0) && VertexCount > 0)
        {
            int triangleCount = VertexCount / 3;
            if (triangleCount > 0)
            {
                _triangles = new List<IndexTriangle>(triangleCount);
                int idx = 0;
                for (int i = 0; i < triangleCount; i++)
                {
                    _triangles.Add(new IndexTriangle(idx++, idx++, idx++));
                }
            }
        }
    }

    private readonly List<XRDataBuffer> _geometryRevisionBuffers = [];

    private void AttachGeometryBufferRevisionTracking(BufferCollection buffers)
    {
        buffers.AttachOwner(this);
        buffers.Added += OnGeometryBufferAdded;
        buffers.Removed += OnGeometryBufferRemoved;
        buffers.Set += OnGeometryBufferReplaced;
        foreach (KeyValuePair<string, XRDataBuffer> entry in buffers)
            TrackGeometryBuffer(entry.Key, entry.Value);
    }

    private void DetachGeometryBufferRevisionTracking(BufferCollection buffers)
    {
        buffers.DetachOwner(this);
        buffers.Added -= OnGeometryBufferAdded;
        buffers.Removed -= OnGeometryBufferRemoved;
        buffers.Set -= OnGeometryBufferReplaced;
        for (int index = _geometryRevisionBuffers.Count - 1; index >= 0; index--)
        {
            XRDataBuffer buffer = _geometryRevisionBuffers[index];
            buffer.RevisionCommitted -= OnGeometryBufferRevisionCommitted;
        }
        _geometryRevisionBuffers.Clear();
    }

    private void OnGeometryBufferAdded(string key, XRDataBuffer buffer)
    {
        TrackGeometryBuffer(key, buffer);
        RefreshConvenienceBufferReference(key);
        if (IsGeometryBufferKey(key))
            AdvanceGeometryRevision();
    }

    private void OnGeometryBufferRemoved(string key, XRDataBuffer buffer)
    {
        if (IsGeometryBufferKey(key))
        {
            UntrackGeometryBufferIfUnused(buffer);
            AdvanceGeometryRevision();
        }
        RefreshConvenienceBufferReference(key);
    }

    private void OnGeometryBufferReplaced(string key, XRDataBuffer previous, XRDataBuffer current)
    {
        if (IsGeometryBufferKey(key))
        {
            UntrackGeometryBufferIfUnused(previous);
            TrackGeometryBuffer(key, current);
            AdvanceGeometryRevision();
        }
        RefreshConvenienceBufferReference(key);
    }

    /// <summary>
    /// Keeps the strongly named buffer references aligned with transactional collection
    /// changes without rebuilding vertex data for every individual callback.
    /// </summary>
    private void RefreshConvenienceBufferReference(string key)
    {
        Buffers.TryGetValue(key, out XRDataBuffer? buffer);
        if (key == ECommonBufferType.Position.ToString())
            PositionsBuffer = buffer;
        else if (key == ECommonBufferType.Normal.ToString())
            NormalsBuffer = buffer;
        else if (key == ECommonBufferType.Tangent.ToString())
            TangentsBuffer = buffer;
        else if (key == ECommonBufferType.InterleavedVertex.ToString())
            InterleavedVertexBuffer = buffer;
        else if (key == ECommonBufferType.BoneInfluenceCoreIndices.ToString())
            BoneInfluenceCoreIndices = buffer;
        else if (key == ECommonBufferType.BoneInfluenceCoreWeights.ToString())
            BoneInfluenceCoreWeights = buffer;
        else if (key == ECommonBufferType.BoneInfluenceSpillHeaders.ToString())
            BoneInfluenceSpillHeaders = buffer;
        else if (key == ECommonBufferType.BoneInfluenceSpillEntries.ToString())
            BoneInfluenceSpillEntries = buffer;
        else if (key == ECommonBufferType.BlendshapeCount.ToString())
            BlendshapeCounts = buffer;
        else if (key == $"{ECommonBufferType.BlendshapeIndices}Buffer")
            BlendshapeIndices = buffer;
        else if (key == $"{ECommonBufferType.BlendshapeDeltas}Buffer")
            BlendshapeDeltas = buffer;
        else if (key == $"{ECommonBufferType.BlendshapeSparseShapeRanges}Buffer")
            BlendshapeSparseShapeRanges = buffer;
        else if (key == $"{ECommonBufferType.BlendshapeSparseRecords}Buffer")
            BlendshapeSparseRecords = buffer;
        else if (key == $"{ECommonBufferType.BlendshapeQuantizedDeltas}Buffer")
            BlendshapeQuantizedDeltas = buffer;
        else if (key == $"{ECommonBufferType.BlendshapeQuantizationMetadata}Buffer")
            BlendshapeQuantizationMetadata = buffer;
        else if (TryGetBufferChannelIndex(key, ECommonBufferType.Color.ToString(), ColorCount, out int colorIndex))
        {
            XRDataBuffer?[]? colorBuffers = ColorBuffers;
            if (colorBuffers is null || (uint)colorBuffers.Length < ColorCount)
                Array.Resize(ref colorBuffers, (int)ColorCount);
            colorBuffers![colorIndex] = buffer;
            ColorBuffers = colorBuffers;
        }
        else if (TryGetBufferChannelIndex(key, ECommonBufferType.TexCoord.ToString(), TexCoordCount, out int texCoordIndex))
        {
            XRDataBuffer?[]? texCoordBuffers = TexCoordBuffers;
            if (texCoordBuffers is null || (uint)texCoordBuffers.Length < TexCoordCount)
                Array.Resize(ref texCoordBuffers, (int)TexCoordCount);
            texCoordBuffers![texCoordIndex] = buffer;
            TexCoordBuffers = texCoordBuffers;
        }
    }

    private static bool TryGetBufferChannelIndex(string key, string prefix, uint channelCount, out int index)
    {
        index = -1;
        return key.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(prefix.Length), out index)
            && index >= 0
            && (uint)index < channelCount;
    }

    private void TrackGeometryBuffer(string key, XRDataBuffer buffer)
    {
        if (!IsGeometryBufferKey(key) || _geometryRevisionBuffers.Contains(buffer))
            return;

        _geometryRevisionBuffers.Add(buffer);
        buffer.RevisionCommitted += OnGeometryBufferRevisionCommitted;
    }

    private void UntrackGeometryBufferIfUnused(XRDataBuffer buffer)
    {
        foreach (KeyValuePair<string, XRDataBuffer> entry in Buffers)
            if (ReferenceEquals(entry.Value, buffer) && IsGeometryBufferKey(entry.Key))
                return;

        if (_geometryRevisionBuffers.Remove(buffer))
            buffer.RevisionCommitted -= OnGeometryBufferRevisionCommitted;
    }

    private void OnGeometryBufferRevisionCommitted(XRDataBuffer buffer, ulong revision)
    {
        _ = buffer;
        _ = revision;
        AdvanceGeometryRevision();
    }

    private static bool IsGeometryBufferKey(string key)
        => key == ECommonBufferType.Position.ToString()
           || key == ECommonBufferType.Normal.ToString()
           || key == ECommonBufferType.Tangent.ToString()
           || key == ECommonBufferType.InterleavedVertex.ToString()
           || key.StartsWith(ECommonBufferType.Color.ToString(), StringComparison.Ordinal)
           || key.StartsWith(ECommonBufferType.TexCoord.ToString(), StringComparison.Ordinal);
}

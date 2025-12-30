using Extensions;
using MemoryPack;
using SimpleScene.Util.ssBVH;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace XREngine.Rendering;

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

    private uint? _normalOffset = 0;
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

    public int VertexCount { get; internal set; }

    private Vertex[] _vertices = [];
    [Browsable(false)]
    [YamlIgnore]
    public Vertex[] Vertices { get => _vertices; private set => SetField(ref _vertices, value); }

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
        set => SetField(ref _points, value);
    }

    [Browsable(false)]
    public List<IndexLine>? Lines
    {
        get => _lines;
        set => SetField(ref _lines, value);
    }

    [Browsable(false)]
    [YamlIgnore]
    public List<IndexTriangle>? Triangles
    {
        get => _triangles;
        set => SetField(ref _triangles, value);
    }

    [Browsable(false)]
    public EPrimitiveType Type
    {
        get => _type;
        set => SetField(ref _type, value);
    }

    private AABB _bounds = new(Vector3.Zero, Vector3.Zero);
    public AABB Bounds
    {
        get => _bounds;
        private set => _bounds = value;
    }

    // Bone usage / skinning
    [MemoryPackIgnore]
    private (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] _utilizedBones = [];
    public (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] UtilizedBones
    {
        get => _utilizedBones;
        set => SetField(ref _utilizedBones, value);
    }
    public bool HasSkinning => _utilizedBones is { Length: > 0 };
    public bool IsSingleBound => UtilizedBones.Length == 1;
    public bool IsUnskinned => UtilizedBones.Length == 0;

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

    // Buffers (per-vertex)
    [MemoryPackIgnore]
    public XRDataBuffer? PositionsBuffer { get; internal set; }
    [MemoryPackIgnore]
    public XRDataBuffer? NormalsBuffer { get; internal set; }
    [MemoryPackIgnore]
    public XRDataBuffer? TangentsBuffer { get; internal set; }
    [MemoryPackIgnore]
    public XRDataBuffer[]? ColorBuffers { get; internal set; } = [];
    [MemoryPackIgnore]
    public XRDataBuffer[]? TexCoordBuffers { get; internal set; } = [];
    [MemoryPackIgnore]
    public XRDataBuffer? InterleavedVertexBuffer { get; private set; }

    // Bone weight indirection
    [MemoryPackIgnore]
    public XRDataBuffer? BoneWeightOffsets { get; private set; }
    [MemoryPackIgnore]
    public XRDataBuffer? BoneWeightCounts { get; private set; }

    // Blendshape indirection
    [MemoryPackIgnore]
    public XRDataBuffer? BlendshapeCounts { get; private set; }

    // Non-per-vertex (skinning / blendshape)
    [MemoryPackIgnore]
    public XRDataBuffer? BoneWeightIndices { get; private set; }
    [MemoryPackIgnore]
    public XRDataBuffer? BoneWeightValues { get; private set; }
    [MemoryPackIgnore]
    public XRDataBuffer? BlendshapeDeltas { get; private set; }
    [MemoryPackIgnore]
    public XRDataBuffer? BlendshapeIndices { get; private set; }

    [MemoryPackIgnore]
    private BufferCollection _buffers = [];

    [MemoryPackIgnore]
    public BufferCollection Buffers
    {
        get => _buffers;
        internal set
        {
            _buffers = value ?? [];
            OnBuffersAssigned();
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
    {
    }

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
                    Debug.LogWarning($"Duplicate or empty blendshape name '{names[i]}' found in mesh {Name}");
            }
        }
    }

    protected override void OnDestroying()
        => Buffers?.ForEach(x => x.Value.Dispose());

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
            BoneWeightOffsets = Buffers.GetValueOrDefault(ECommonBufferType.BoneMatrixOffset.ToString());
            BoneWeightCounts = Buffers.GetValueOrDefault(ECommonBufferType.BoneMatrixCount.ToString());
            BoneWeightIndices = Buffers.GetValueOrDefault($"{ECommonBufferType.BoneMatrixIndices}Buffer");
            BoneWeightValues = Buffers.GetValueOrDefault($"{ECommonBufferType.BoneMatrixWeights}Buffer");
        }

        if (HasBlendshapes)
        {
            BlendshapeCounts = Buffers.GetValueOrDefault(ECommonBufferType.BlendshapeCount.ToString());
            BlendshapeIndices = Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeIndices}Buffer");
            BlendshapeDeltas = Buffers.GetValueOrDefault($"{ECommonBufferType.BlendshapeDeltas}Buffer");
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
                    v.Tangent = GetTangent(i);

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
}
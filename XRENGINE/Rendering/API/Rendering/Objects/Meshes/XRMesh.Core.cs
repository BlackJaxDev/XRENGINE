using Extensions;
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

public partial class XRMesh : XRAsset
{
    private delegate void DelVertexAction(XRMesh @this, int index, int remappedIndex, Vertex vtx, Matrix4x4? dataTransform);

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
    public Vertex[] Vertices { get => _vertices; private set => SetField(ref _vertices, value); }

    // Primitive index storage
    private List<int>? _points;
    private List<IndexLine>? _lines;
    private List<IndexTriangle>? _triangles;
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
    private readonly Dictionary<string, int> _blendshapeNameToIndex = [];
    [Browsable(false)]
    public uint BlendshapeCount => (uint)(BlendshapeNames?.Length ?? 0);
    [Browsable(false)]
    public bool HasBlendshapes => BlendshapeCount > 0;

    // Buffers (per-vertex)
    public XRDataBuffer? PositionsBuffer { get; internal set; }
    public XRDataBuffer? NormalsBuffer { get; internal set; }
    public XRDataBuffer? TangentsBuffer { get; internal set; }
    public XRDataBuffer[]? ColorBuffers { get; internal set; } = [];
    public XRDataBuffer[]? TexCoordBuffers { get; internal set; } = [];
    public XRDataBuffer? InterleavedVertexBuffer { get; private set; }

    // Bone weight indirection
    public XRDataBuffer? BoneWeightOffsets { get; private set; }
    public XRDataBuffer? BoneWeightCounts { get; private set; }

    // Blendshape indirection
    public XRDataBuffer? BlendshapeCounts { get; private set; }

    // Non-per-vertex (skinning / blendshape)
    public XRDataBuffer? BoneWeightIndices { get; private set; }
    public XRDataBuffer? BoneWeightValues { get; private set; }
    public XRDataBuffer? BlendshapeDeltas { get; private set; }
    public XRDataBuffer? BlendshapeIndices { get; private set; }

    public BufferCollection Buffers { get; internal set; } = [];

    // Weight stats
    private int _maxWeightCount;
    public int MaxWeightCount => _maxWeightCount;

    // BVH / spatial
    private BVH<XREngine.Data.Geometry.Triangle>? _bvhTree;
    private bool _generating;

    // SDF
    public XRTexture3D? SignedDistanceField { get; internal set; }

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
}
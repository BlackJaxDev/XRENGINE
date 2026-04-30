using MemoryPack;
using XREngine.Data.Core;

namespace XREngine.Rendering.Meshlets;

public enum MeshletBuildMode
{
    Dense = 0,
    Scan = 1,
    Flex = 2,
    Spatial = 3,
}

public enum MeshOptimizerLodMode
{
    Simplify = 0,
    SimplifyWithAttributes = 1,
    SimplifyWithUpdate = 2,
    SimplifySloppy = 3,
}

[Flags]
public enum MeshOptimizerSimplifyOptions : uint
{
    None = 0,
    LockBorder = 1u << 0,
    Sparse = 1u << 1,
    ErrorAbsolute = 1u << 2,
    Prune = 1u << 3,
    Regularize = 1u << 4,
    Permissive = 1u << 5,
    RegularizeLight = 1u << 6,
}

[Flags]
public enum MeshOptimizerVertexLockFlags : byte
{
    None = 0,
    Lock = 1 << 0,
    Protect = 1 << 1,
    Priority = 1 << 2,
}

[MemoryPackable(GenerateType.NoGenerate)]
public partial class MeshOptimizerSubMeshSettings : XRBase
{
    private MeshletGenerationSettings _meshlets = new();
    private MeshLodGenerationSettings _lods = new();

    public MeshletGenerationSettings Meshlets
    {
        get => _meshlets;
        set => SetField(ref _meshlets, value ?? new MeshletGenerationSettings());
    }

    public MeshLodGenerationSettings Lods
    {
        get => _lods;
        set => SetField(ref _lods, value ?? new MeshLodGenerationSettings());
    }
}

[MemoryPackable(GenerateType.NoGenerate)]
public partial class MeshletGenerationSettings : XRBase
{
    private bool _enabled;
    private MeshletBuildMode _buildMode = MeshletBuildMode.Dense;
    private uint _maxVertices = 64u;
    private uint _minTriangles = 32u;
    private uint _maxTriangles = 124u;
    private float _coneWeight = 0.25f;
    private float _splitFactor = 2.0f;
    private float _fillWeight = 0.5f;
    private bool _optimizeMeshlets = true;
    private int _optimizeLevel = 0;
    private bool _computeBounds = true;
    private bool _encodeMeshlets;
    private bool _encodeVertexReferences = true;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public MeshletBuildMode BuildMode
    {
        get => _buildMode;
        set => SetField(ref _buildMode, value);
    }

    public uint MaxVertices
    {
        get => _maxVertices;
        set => SetField(ref _maxVertices, Math.Clamp(value, 1u, 256u));
    }

    public uint MinTriangles
    {
        get => _minTriangles;
        set => SetField(ref _minTriangles, Math.Clamp(value, 1u, 512u));
    }

    public uint MaxTriangles
    {
        get => _maxTriangles;
        set => SetField(ref _maxTriangles, Math.Clamp(value, 1u, 512u));
    }

    public float ConeWeight
    {
        get => _coneWeight;
        set => SetField(ref _coneWeight, Math.Clamp(value, 0.0f, 1.0f));
    }

    public float SplitFactor
    {
        get => _splitFactor;
        set => SetField(ref _splitFactor, Math.Max(0.0f, value));
    }

    public float FillWeight
    {
        get => _fillWeight;
        set => SetField(ref _fillWeight, Math.Max(0.0f, value));
    }

    public bool OptimizeMeshlets
    {
        get => _optimizeMeshlets;
        set => SetField(ref _optimizeMeshlets, value);
    }

    public int OptimizeLevel
    {
        get => _optimizeLevel;
        set => SetField(ref _optimizeLevel, Math.Clamp(value, 0, 9));
    }

    public bool ComputeBounds
    {
        get => _computeBounds;
        set => SetField(ref _computeBounds, value);
    }

    public bool EncodeMeshlets
    {
        get => _encodeMeshlets;
        set => SetField(ref _encodeMeshlets, value);
    }

    public bool EncodeVertexReferences
    {
        get => _encodeVertexReferences;
        set => SetField(ref _encodeVertexReferences, value);
    }
}

[MemoryPackable(GenerateType.NoGenerate)]
public partial class MeshLodGenerationSettings : XRBase
{
    private bool _enabled;
    private MeshOptimizerLodMode _mode = MeshOptimizerLodMode.SimplifyWithAttributes;
    private int _additionalLodCount = 2;
    private float _firstLodIndexRatio = 0.5f;
    private float _lodRatioScale = 0.5f;
    private float _targetError = 0.01f;
    private float _firstLodDistance = 20.0f;
    private float _lodDistanceScale = 2.0f;
    private bool _reusePreviousLodAsSource = true;
    private MeshOptimizerSimplifyOptions _options = MeshOptimizerSimplifyOptions.Prune;
    private bool _useNormals = true;
    private float _normalWeight = 0.5f;
    private bool _useTangents = true;
    private float _tangentWeight = 0.25f;
    private bool _useTexCoords = true;
    private float _texCoordWeight = 1.0f;
    private bool _useColors;
    private float _colorWeight = 1.0f;
    private bool _protectAttributeSeams;
    private bool _prioritizeBorderVertices;
    private bool _lockWeightedVertices;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public MeshOptimizerLodMode Mode
    {
        get => _mode;
        set => SetField(ref _mode, value);
    }

    public int AdditionalLodCount
    {
        get => _additionalLodCount;
        set => SetField(ref _additionalLodCount, Math.Clamp(value, 0, 8));
    }

    public float FirstLodIndexRatio
    {
        get => _firstLodIndexRatio;
        set => SetField(ref _firstLodIndexRatio, Math.Clamp(value, 0.0f, 1.0f));
    }

    public float LodRatioScale
    {
        get => _lodRatioScale;
        set => SetField(ref _lodRatioScale, Math.Clamp(value, 0.0f, 1.0f));
    }

    public float TargetError
    {
        get => _targetError;
        set => SetField(ref _targetError, value <= 0.0f ? float.MaxValue : value);
    }

    public float FirstLodDistance
    {
        get => _firstLodDistance;
        set => SetField(ref _firstLodDistance, Math.Max(0.0f, value));
    }

    public float LodDistanceScale
    {
        get => _lodDistanceScale;
        set => SetField(ref _lodDistanceScale, Math.Max(1.0f, value));
    }

    public bool ReusePreviousLodAsSource
    {
        get => _reusePreviousLodAsSource;
        set => SetField(ref _reusePreviousLodAsSource, value);
    }

    public MeshOptimizerSimplifyOptions Options
    {
        get => _options;
        set => SetField(ref _options, value);
    }

    public bool UseNormals
    {
        get => _useNormals;
        set => SetField(ref _useNormals, value);
    }

    public float NormalWeight
    {
        get => _normalWeight;
        set => SetField(ref _normalWeight, Math.Max(0.0f, value));
    }

    public bool UseTangents
    {
        get => _useTangents;
        set => SetField(ref _useTangents, value);
    }

    public float TangentWeight
    {
        get => _tangentWeight;
        set => SetField(ref _tangentWeight, Math.Max(0.0f, value));
    }

    public bool UseTexCoords
    {
        get => _useTexCoords;
        set => SetField(ref _useTexCoords, value);
    }

    public float TexCoordWeight
    {
        get => _texCoordWeight;
        set => SetField(ref _texCoordWeight, Math.Max(0.0f, value));
    }

    public bool UseColors
    {
        get => _useColors;
        set => SetField(ref _useColors, value);
    }

    public float ColorWeight
    {
        get => _colorWeight;
        set => SetField(ref _colorWeight, Math.Max(0.0f, value));
    }

    public bool ProtectAttributeSeams
    {
        get => _protectAttributeSeams;
        set => SetField(ref _protectAttributeSeams, value);
    }

    public bool PrioritizeBorderVertices
    {
        get => _prioritizeBorderVertices;
        set => SetField(ref _prioritizeBorderVertices, value);
    }

    public bool LockWeightedVertices
    {
        get => _lockWeightedVertices;
        set => SetField(ref _lockWeightedVertices, value);
    }
}

public readonly record struct MeshOptimizerMeshletStats(
    int MeshletCount,
    int VertexReferenceCount,
    int TriangleByteCount,
    int EncodedByteCount);

public readonly record struct MeshOptimizerLodStats(
    int SourceTriangleCount,
    int ResultTriangleCount,
    float TargetIndexRatio,
    float NormalizedError,
    float ObjectSpaceError);
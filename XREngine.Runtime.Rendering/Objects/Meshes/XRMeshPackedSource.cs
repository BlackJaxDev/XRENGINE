using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering;

/// <summary>
/// Struct-of-arrays construction source for one indexed mesh. Importers append
/// vertex attributes, bone influences and blendshape deltas in packed form, and
/// <see cref="XRMesh(XRMeshPackedSource, IReadOnlyList{int})"/> writes the mesh's
/// attribute, skinning and blendshape buffers from it without creating
/// per-vertex <see cref="Data.Rendering.Vertex"/> objects, weight dictionaries or
/// blendshape vertex copies.
/// </summary>
/// <remarks>
/// Influences and blendshape deltas always belong to the most recently added
/// vertex. Bone indices address <see cref="Bones"/>; blendshape indices address
/// <see cref="BlendshapeNames"/>. A source is single-threaded and is discarded
/// once the mesh is built.
/// </remarks>
public sealed class XRMeshPackedSource
{
    private Vector3[] _positions;
    private Vector3[] _normals;
    private Vector4[] _tangents;
    private Vector2[] _texCoords;
    private Vector4[] _colors;
    private int[] _influenceStarts;
    private int[] _influenceBones = [];
    private float[] _influenceWeights = [];
    private int _influenceCount;
    private int[] _blendshapeStarts;
    private int[] _blendshapeShapes = [];
    private Vector3[] _blendshapePositionDeltas = [];
    private Vector3[] _blendshapeNormalDeltas = [];
    private Vector3[] _blendshapeTangentDeltas = [];
    private int _blendshapeEntryCount;
    private readonly List<(TransformBase tfm, Matrix4x4 invBindWorldMtx)> _bones = [];
    private readonly Dictionary<TransformBase, int> _boneIndices = new(ReferenceEqualityComparer.Instance);

    public XRMeshPackedSource(
        bool hasNormals,
        bool hasTangents,
        int texCoordSetCount,
        int colorSetCount,
        int vertexCapacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(texCoordSetCount);
        ArgumentOutOfRangeException.ThrowIfNegative(colorSetCount);
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCapacity);
        HasNormals = hasNormals;
        HasTangents = hasTangents;
        TexCoordSetCount = texCoordSetCount;
        ColorSetCount = colorSetCount;
        int capacity = Math.Max(4, vertexCapacity);
        _positions = new Vector3[capacity];
        _normals = hasNormals ? new Vector3[capacity] : [];
        _tangents = hasTangents ? new Vector4[capacity] : [];
        _texCoords = new Vector2[capacity * texCoordSetCount];
        _colors = new Vector4[capacity * colorSetCount];
        _influenceStarts = new int[capacity + 1];
        _blendshapeStarts = new int[capacity + 1];
    }

    public bool HasNormals { get; }
    public bool HasTangents { get; }
    public int TexCoordSetCount { get; }
    public int ColorSetCount { get; }
    public int VertexCount { get; private set; }

    /// <summary>Bone palette that influence bone indices address, in insertion order.</summary>
    public IReadOnlyList<(TransformBase tfm, Matrix4x4 invBindWorldMtx)> Bones => _bones;
    public bool HasInfluences => _influenceCount > 0;

    /// <summary>Blendshape names that delta shape indices address; null when the mesh has none.</summary>
    public string[]? BlendshapeNames { get; set; }
    public bool HasBlendshapeDeltas => _blendshapeEntryCount > 0;

    /// <summary>
    /// Appends a vertex. Absent attributes (per the constructor flags) are
    /// ignored; texture-coordinate and color spans shorter than the set counts
    /// leave the remaining sets at zero.
    /// </summary>
    public int AddVertex(
        Vector3 position,
        Vector3 normal,
        Vector4 tangentWithSign,
        ReadOnlySpan<Vector2> texCoords,
        ReadOnlySpan<Vector4> colors)
    {
        int vertex = VertexCount;
        EnsureVertexCapacity(vertex + 1);
        _positions[vertex] = position;
        if (HasNormals)
            _normals[vertex] = normal;
        if (HasTangents)
            _tangents[vertex] = tangentWithSign;
        int texBase = vertex * TexCoordSetCount;
        for (int set = 0; set < TexCoordSetCount; set++)
            _texCoords[texBase + set] = set < texCoords.Length ? texCoords[set] : Vector2.Zero;
        int colorBase = vertex * ColorSetCount;
        for (int set = 0; set < ColorSetCount; set++)
            _colors[colorBase + set] = set < colors.Length ? colors[set] : Vector4.Zero;

        VertexCount = vertex + 1;
        _influenceStarts[VertexCount] = _influenceCount;
        _blendshapeStarts[VertexCount] = _blendshapeEntryCount;
        return vertex;
    }

    /// <summary>Returns the palette index of a bone, adding it on first use.</summary>
    public int AddBone(TransformBase bone, Matrix4x4 inverseBind)
    {
        ArgumentNullException.ThrowIfNull(bone);
        if (_boneIndices.TryGetValue(bone, out int index))
            return index;
        index = _bones.Count;
        _bones.Add((bone, inverseBind));
        _boneIndices.Add(bone, index);
        return index;
    }

    /// <summary>Adds an influence of a palette bone to the most recently added vertex.</summary>
    public void AddInfluence(int boneIndex, float weight)
    {
        if (VertexCount == 0)
            throw new InvalidOperationException("Add a vertex before its influences.");
        if ((uint)boneIndex >= (uint)_bones.Count)
            throw new ArgumentOutOfRangeException(nameof(boneIndex));
        if (_influenceCount == _influenceBones.Length)
        {
            int capacity = Math.Max(16, _influenceBones.Length * 2);
            Array.Resize(ref _influenceBones, capacity);
            Array.Resize(ref _influenceWeights, capacity);
        }
        _influenceBones[_influenceCount] = boneIndex;
        _influenceWeights[_influenceCount] = weight;
        _influenceCount++;
        _influenceStarts[VertexCount] = _influenceCount;
    }

    /// <summary>
    /// Adds one blendshape's deltas (target minus base) for the most recently
    /// added vertex. Shapes may be added in any order; a shape should appear at
    /// most once per vertex.
    /// </summary>
    public void AddBlendshapeDelta(int shapeIndex, Vector3 positionDelta, Vector3 normalDelta, Vector3 tangentDelta)
    {
        if (VertexCount == 0)
            throw new InvalidOperationException("Add a vertex before its blendshape deltas.");
        ArgumentOutOfRangeException.ThrowIfNegative(shapeIndex);
        if (_blendshapeEntryCount == _blendshapeShapes.Length)
        {
            int capacity = Math.Max(16, _blendshapeShapes.Length * 2);
            Array.Resize(ref _blendshapeShapes, capacity);
            Array.Resize(ref _blendshapePositionDeltas, capacity);
            Array.Resize(ref _blendshapeNormalDeltas, capacity);
            Array.Resize(ref _blendshapeTangentDeltas, capacity);
        }
        _blendshapeShapes[_blendshapeEntryCount] = shapeIndex;
        _blendshapePositionDeltas[_blendshapeEntryCount] = positionDelta;
        _blendshapeNormalDeltas[_blendshapeEntryCount] = normalDelta;
        _blendshapeTangentDeltas[_blendshapeEntryCount] = tangentDelta;
        _blendshapeEntryCount++;
        _blendshapeStarts[VertexCount] = _blendshapeEntryCount;
    }

    internal ReadOnlySpan<Vector3> Positions => _positions.AsSpan(0, VertexCount);
    internal ReadOnlySpan<Vector3> Normals => HasNormals ? _normals.AsSpan(0, VertexCount) : [];
    internal ReadOnlySpan<Vector4> Tangents => HasTangents ? _tangents.AsSpan(0, VertexCount) : [];
    internal Vector2 GetTexCoord(int vertex, int set) => _texCoords[vertex * TexCoordSetCount + set];
    internal Vector4 GetColor(int vertex, int set) => _colors[vertex * ColorSetCount + set];

    /// <summary>The vertex's influences as palette bone indices and weights.</summary>
    internal void GetInfluences(int vertex, out ReadOnlySpan<int> bones, out ReadOnlySpan<float> weights)
    {
        int start = _influenceStarts[vertex];
        int count = _influenceStarts[vertex + 1] - start;
        bones = _influenceBones.AsSpan(start, count);
        weights = _influenceWeights.AsSpan(start, count);
    }

    /// <summary>The vertex's blendshape entries: shape indices and their deltas.</summary>
    internal void GetBlendshapeEntries(
        int vertex,
        out ReadOnlySpan<int> shapes,
        out ReadOnlySpan<Vector3> positionDeltas,
        out ReadOnlySpan<Vector3> normalDeltas,
        out ReadOnlySpan<Vector3> tangentDeltas)
    {
        int start = _blendshapeStarts[vertex];
        int count = _blendshapeStarts[vertex + 1] - start;
        shapes = _blendshapeShapes.AsSpan(start, count);
        positionDeltas = _blendshapePositionDeltas.AsSpan(start, count);
        normalDeltas = _blendshapeNormalDeltas.AsSpan(start, count);
        tangentDeltas = _blendshapeTangentDeltas.AsSpan(start, count);
    }

    private void EnsureVertexCapacity(int count)
    {
        if (count <= _positions.Length)
            return;
        int capacity = Math.Max(count, _positions.Length * 2);
        Array.Resize(ref _positions, capacity);
        if (HasNormals)
            Array.Resize(ref _normals, capacity);
        if (HasTangents)
            Array.Resize(ref _tangents, capacity);
        Array.Resize(ref _texCoords, capacity * TexCoordSetCount);
        Array.Resize(ref _colors, capacity * ColorSetCount);
        Array.Resize(ref _influenceStarts, capacity + 1);
        Array.Resize(ref _blendshapeStarts, capacity + 1);
    }
}

using System.Numerics;

namespace XREngine.Fbx;

public enum FbxLayerElementMappingType
{
    Unknown,
    ByControlPoint,
    ByPolygonVertex,
    ByPolygon,
    AllSame,
}

public enum FbxLayerElementReferenceType
{
    Unknown,
    Direct,
    Index,
    IndexToDirect,
}

public sealed record FbxLayerElement<T>(
    string Name,
    FbxLayerElementMappingType MappingType,
    FbxLayerElementReferenceType ReferenceType,
    IReadOnlyList<T> DirectValues,
    IReadOnlyList<int> Indices,
    int NodeIndex);

public sealed record FbxMeshGeometry(
    long ObjectId,
    string Name,
    string GeometryType,
    int ObjectIndex,
    int NodeIndex,
    IReadOnlyList<Vector3> ControlPoints,
    IReadOnlyList<int> PolygonVertexIndices,
    IReadOnlyList<FbxLayerElement<Vector3>> Normals,
    IReadOnlyList<FbxLayerElement<Vector3>> Tangents,
    IReadOnlyList<FbxLayerElement<Vector2>> TextureCoordinates,
    IReadOnlyList<FbxLayerElement<Vector4>> Colors,
    FbxLayerElement<int>? Materials);

public sealed class FbxGeometryDocument
{
    private readonly Dictionary<long, FbxMeshGeometry> _meshesByObjectId;

    internal FbxGeometryDocument(Dictionary<long, FbxMeshGeometry> meshesByObjectId)
        => _meshesByObjectId = meshesByObjectId;

    public IReadOnlyDictionary<long, FbxMeshGeometry> MeshesByObjectId => _meshesByObjectId;

    public bool TryGetMeshGeometry(long objectId, out FbxMeshGeometry geometry)
        => _meshesByObjectId.TryGetValue(objectId, out geometry!);
}
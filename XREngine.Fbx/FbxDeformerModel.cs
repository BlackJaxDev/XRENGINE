using System.Numerics;

namespace XREngine.Fbx;

public sealed record FbxClusterBinding(
    long ClusterObjectId,
    long BoneModelObjectId,
    string BoneName,
    Matrix4x4 TransformMatrix,
    Matrix4x4 TransformLinkMatrix,
    Matrix4x4 InverseBindMatrix,
    IReadOnlyDictionary<int, float> ControlPointWeights);

public sealed record FbxSkinBinding(
    long GeometryObjectId,
    long SkinObjectId,
    string Name,
    IReadOnlyList<FbxClusterBinding> Clusters);

public sealed record FbxBlendShapeChannelBinding(
    long ChannelObjectId,
    long GeometryObjectId,
    string Name,
    float DefaultDeformPercent,
    float FullWeight,
    IReadOnlyDictionary<int, Vector3> PositionDeltasByControlPoint,
    IReadOnlyDictionary<int, Vector3> NormalDeltasByControlPoint);

public sealed class FbxDeformerDocument
{
    private readonly Dictionary<long, FbxSkinBinding> _skinsByGeometryObjectId;
    private readonly Dictionary<long, FbxBlendShapeChannelBinding[]> _blendShapeChannelsByGeometryObjectId;
    private readonly Dictionary<long, FbxBlendShapeChannelBinding> _blendShapeChannelsByObjectId;

    internal FbxDeformerDocument(
        Dictionary<long, FbxSkinBinding> skinsByGeometryObjectId,
        Dictionary<long, FbxBlendShapeChannelBinding[]> blendShapeChannelsByGeometryObjectId,
        Dictionary<long, FbxBlendShapeChannelBinding> blendShapeChannelsByObjectId)
    {
        _skinsByGeometryObjectId = skinsByGeometryObjectId;
        _blendShapeChannelsByGeometryObjectId = blendShapeChannelsByGeometryObjectId;
        _blendShapeChannelsByObjectId = blendShapeChannelsByObjectId;
    }

    public IReadOnlyDictionary<long, FbxSkinBinding> SkinsByGeometryObjectId => _skinsByGeometryObjectId;
    public IReadOnlyDictionary<long, FbxBlendShapeChannelBinding[]> BlendShapeChannelsByGeometryObjectId => _blendShapeChannelsByGeometryObjectId;

    public bool TryGetSkinBinding(long geometryObjectId, out FbxSkinBinding skinBinding)
        => _skinsByGeometryObjectId.TryGetValue(geometryObjectId, out skinBinding!);

    public IReadOnlyList<FbxBlendShapeChannelBinding> GetBlendShapeChannels(long geometryObjectId)
        => _blendShapeChannelsByGeometryObjectId.TryGetValue(geometryObjectId, out FbxBlendShapeChannelBinding[]? channels)
            ? channels
            : [];

    public bool TryGetBlendShapeChannel(long channelObjectId, out FbxBlendShapeChannelBinding channelBinding)
        => _blendShapeChannelsByObjectId.TryGetValue(channelObjectId, out channelBinding!);
}
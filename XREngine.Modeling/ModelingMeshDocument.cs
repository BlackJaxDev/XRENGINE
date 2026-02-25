using System.Numerics;

namespace XREngine.Modeling;

public sealed class ModelingMeshDocument
{
    public List<Vector3> Positions { get; set; } = [];
    public List<int> TriangleIndices { get; set; } = [];
    public List<Vector3>? Normals { get; set; }
    public List<Vector3>? Tangents { get; set; }
    public List<List<Vector2>>? TexCoordChannels { get; set; }
    public List<List<Vector4>>? ColorChannels { get; set; }
    public List<ModelingSkinBone>? SkinBones { get; set; }
    public List<List<ModelingSkinWeight>>? SkinWeights { get; set; }
    public List<ModelingBlendshapeChannel>? BlendshapeChannels { get; set; }
    public ModelingMeshMetadata Metadata { get; set; } = new();
}

public sealed class ModelingSkinBone
{
    public string? Name { get; set; }
    public Matrix4x4 InverseBindMatrix { get; set; } = Matrix4x4.Identity;
}

public sealed record ModelingSkinWeight(int BoneIndex, float Weight);

public sealed class ModelingBlendshapeChannel
{
    public string Name { get; set; } = string.Empty;
    public List<Vector3> PositionDeltas { get; set; } = [];
    public List<Vector3>? NormalDeltas { get; set; }
    public List<Vector3>? TangentDeltas { get; set; }
}

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
    public ModelingMeshMetadata Metadata { get; set; } = new();
}

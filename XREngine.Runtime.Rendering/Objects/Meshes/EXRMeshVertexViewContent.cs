namespace XREngine.Rendering;

/// <summary>Vertex data an <see cref="XRMeshVertexView"/> materializes from the mesh's packed buffers.</summary>
[Flags]
public enum EXRMeshVertexViewContent
{
    None = 0,
    Positions = 1 << 0,
    Normals = 1 << 1,
    Tangents = 1 << 2,
    TexCoords = 1 << 3,
    Colors = 1 << 4,
    /// <summary>Bone weights decoded from the packed Core4 + spill skinning buffers.</summary>
    Weights = 1 << 5,
    /// <summary>Per-vertex blendshape targets decoded from the packed active-list buffers.</summary>
    Blendshapes = 1 << 6,
    /// <summary>Every surface attribute: positions, normals, tangents, texture coordinates and colors.</summary>
    Attributes = Positions | Normals | Tangents | TexCoords | Colors,
    All = Attributes | Weights | Blendshapes,
}

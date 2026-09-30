using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Owned geometry and optional texture for one runtime model component.</summary>
public sealed record RuntimeVrModelComponentData(
    Vertex[] Vertices,
    List<ushort> TriangleIndices,
    RuntimeVrTextureData? Texture);

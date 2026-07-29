using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Scene-owned geometry/deformation offsets referenced by indirect producers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedSceneGeometryOffsets(
    uint VertexOffset,
    uint PreviousVertexOffset,
    uint IndexOffset,
    uint WeightOffset,
    uint PaletteOffset,
    uint MeshletOffset,
    uint MeshletCount);

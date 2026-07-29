using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Canonical 64-byte output vertex shared by visibility, reconstruction,
/// shadows, velocity, and material shading. Direction vectors use octahedral
/// packing, UVs use packed half pairs, and colors use RGBA8.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct AdvancedDeformedVertex
{
    /// <summary>
    /// Stable identity for the canonical packed deformation layout. Source
    /// mesh layouts are normalized into this layout before dispatch.
    /// </summary>
    public const ulong CanonicalLayoutId = 0x4144_5631_0040_0001UL;

    public Vector3 Position;
    public uint NormalOct;
    public uint TangentOctAndSign;
    public uint TexCoord0Half;
    public uint TexCoord1Half;
    public uint Color0Rgba8;
    public uint Color1Rgba8;
    public uint Custom0;
    public uint SourceVertex;
    public uint Flags;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
}

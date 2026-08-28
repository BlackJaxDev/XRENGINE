using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Canonical meshlet descriptor consumed with the three immutable geometry streams.
/// The layout is deliberately byte-for-byte stable with the GPU visibility ABI.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedMeshletDescriptor
{
    public Vector4 BoundsSphere;
    public uint VertexOffset;
    public uint TriangleByteOffset;
    public uint VertexCount;
    public uint TriangleCount;
    public Vector4 Cone;
    public Vector4 ConeApex;
    public uint PackedCone;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
}

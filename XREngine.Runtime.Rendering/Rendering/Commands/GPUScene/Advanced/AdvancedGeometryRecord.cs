using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Canonical immutable geometry row used by visibility and shading.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedGeometryRecord
{
    public AdvancedBufferReference CurrentVertexData;
    public AdvancedBufferReference PreviousVertexData;
    public AdvancedBufferReference IndexData;
    public AdvancedBufferReference MeshletData;
    public AdvancedGpuHandle FallbackGeometry;
    public uint VertexBase;
    public uint VertexCount;
    public uint IndexBase;
    public uint IndexCount;
    public uint MeshletFirst;
    public uint MeshletCount;
    public ulong VertexLayoutId;
    public uint ReservedLayout0;
    public uint ReservedLayout1;
    public Vector4 BoundsSphere;
    public Vector4 BoundsMin;
    public Vector4 BoundsMax;
    public uint MaterialSectionFirst;
    public uint MaterialSectionCount;
    public EPrimitiveType PrimitiveTopology;
    public EAdvancedGeometrySource Source;
    public EAdvancedGeometryResidency Residency;
    public EAdvancedMissingGeometryBehavior MissingBehavior;
    public uint CookedLayoutVersion;
    public uint Flags;

    public readonly bool IsResident
        => Residency == EAdvancedGeometryResidency.Resident;
}

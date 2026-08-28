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
    /// <summary>Fixed 80-byte <see cref="AdvancedMeshletDescriptor"/> records.</summary>
    public AdvancedBufferReference MeshletDescriptors;
    /// <summary>Meshlet-local vertex indices as unsigned 32-bit words.</summary>
    public AdvancedBufferReference MeshletVertexIndices;
    /// <summary>Triangle bytes packed into padded unsigned 32-bit words.</summary>
    public AdvancedBufferReference MeshletTriangleWords;
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

    /// <summary>
    /// Source-compatibility alias for code which only needs to know whether a
    /// meshlet payload exists. New consumers must bind all three meshlet streams.
    /// </summary>
    public readonly AdvancedBufferReference MeshletData => MeshletDescriptors;
}

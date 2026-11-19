using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Visibility range key. Material instance identity is deliberately absent.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedIndirectRangeKey(
    AdvancedGpuHandle Geometry,
    uint RasterStateClass,
    EAdvancedMaterialCoverageMode Coverage,
    uint CullMode,
    uint PrimitiveTopology,
    EAdvancedGeometryProducer Producer,
    bool UsesDeformedVertexSource)
{
    /// <summary>
    /// Returns the geometry handle that a range or bin key holds for one payload. Indexed and
    /// meshlet producers read their geometry from the shared atlases through each payload
    /// (index atlas, static vertex atlas, meshlet descriptor table), so their key holds no
    /// geometry and payloads of different meshes share one range, one bin and one indirect
    /// draw. CPU-direct producers bind the vertex and index buffers of one mesh, so their key
    /// keeps the geometry handle.
    /// </summary>
    public static AdvancedGpuHandle RangeGeometry(EAdvancedGeometryProducer producer, AdvancedGpuHandle geometry)
        => producer is EAdvancedGeometryProducer.IndirectIndexed
            or EAdvancedGeometryProducer.StaticMeshlet
            or EAdvancedGeometryProducer.SkinnedMeshlet
            ? default
            : geometry;
}

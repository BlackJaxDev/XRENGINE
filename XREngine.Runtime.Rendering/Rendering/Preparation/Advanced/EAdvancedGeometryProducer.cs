namespace XREngine.Rendering;

/// <summary>
/// Geometry producer specialization. Every producer emits the same visibility
/// identity and barycentric payload.
/// </summary>
public enum EAdvancedGeometryProducer : uint
{
    StaticMeshlet = 0u,
    SkinnedMeshlet = 1u,
    IndirectIndexed = 2u,
    CpuDirectStaticIndexed = 3u,
    CpuDirectPreSkinned = 4u,

    TraditionalIndirect = IndirectIndexed,

    CpuDirectDiagnostic = CpuDirectStaticIndexed,
}

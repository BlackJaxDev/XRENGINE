namespace XREngine.Rendering;

/// <summary>
/// Geometry producer specialization. Every producer emits the same visibility
/// identity and barycentric payload.
/// </summary>
public enum EAdvancedGeometryProducer : uint
{
    StaticMeshlet = 0u,
    SkinnedMeshlet = 1u,
    TraditionalIndirect = 2u,
    CpuDirectDiagnostic = 3u,
}

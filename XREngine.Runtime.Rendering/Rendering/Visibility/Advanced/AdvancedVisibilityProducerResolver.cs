using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Maps the canonical mesh-submission strategy to a visibility geometry
/// producer without changing the payload or downstream shading architecture.
/// </summary>
public static class AdvancedVisibilityProducerResolver
{
    public static EAdvancedGeometryProducer Resolve(
        EMeshSubmissionStrategy strategy,
        in AdvancedVisibilityPayload payload)
    {
        if (payload.ForceCpuDiagnostic ||
            strategy == EMeshSubmissionStrategy.CpuDirect)
        {
            return payload.Skinned
                ? EAdvancedGeometryProducer.CpuDirectPreSkinned
                : EAdvancedGeometryProducer.CpuDirectStaticIndexed;
        }

        if (strategy.IsAnyMeshletStrategy() &&
            payload.MeshletsResident)
        {
            return payload.Skinned
                ? EAdvancedGeometryProducer.SkinnedMeshlet
                : EAdvancedGeometryProducer.StaticMeshlet;
        }

        return EAdvancedGeometryProducer.IndirectIndexed;
    }
}

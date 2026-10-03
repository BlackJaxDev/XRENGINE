namespace XREngine.Rendering.Commands;

/// <summary>Frozen ownership of the resident primitives belonging to one authored mesh command.</summary>
public enum EGpuMeshSubmissionSourceOwnership
{
    Missing,
    Gpu,
    ExplicitCpu,
    MixedExplicitOwnership,
    IncompleteSource,
}

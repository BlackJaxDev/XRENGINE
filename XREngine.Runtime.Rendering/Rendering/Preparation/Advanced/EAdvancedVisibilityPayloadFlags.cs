namespace XREngine.Rendering;

/// <summary>
/// GPU-stable producer flags for a visibility payload.
/// </summary>
[Flags]
public enum EAdvancedVisibilityPayloadFlags : uint
{
    None = 0u,
    Skinned = 1u << 0,
    MeshletsResident = 1u << 1,
    ForceCpuDiagnostic = 1u << 2,
    TemporalReasonMask = AdvancedReconstructionTemporalFlags.VelocityReasonMask,
}

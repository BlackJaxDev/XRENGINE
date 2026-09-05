using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// GPU overlay row indexed by canonical physical draw row. It selects exact
/// deformation output ranges while leaving immutable geometry topology intact.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public readonly record struct AdvancedPreparedDrawDeformationRecord(
    AdvancedGpuHandle Geometry,
    AdvancedGpuHandle Deformation,
    uint CurrentVertexOffset,
    uint PreviousVertexOffset,
    uint VertexCount,
    EAdvancedPreparedDrawDeformationFlags Flags)
{
    public bool Active => (Flags & EAdvancedPreparedDrawDeformationFlags.Active) != 0;
    public bool PreviousValid => (Flags & EAdvancedPreparedDrawDeformationFlags.PreviousValid) != 0;
    public bool TemporalStatePresent
        => (Flags & EAdvancedPreparedDrawDeformationFlags.TemporalStatePresent) != 0;
    public EAdvancedVelocityValidityReason TemporalReason
        => AdvancedReconstructionTemporalFlags.DecodeVelocityReason((uint)Flags);
}

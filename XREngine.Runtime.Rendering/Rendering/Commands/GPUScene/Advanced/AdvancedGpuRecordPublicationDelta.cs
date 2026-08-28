using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// One bounded, allocation-free publication-journal entry for a stable logical
/// handle. Dense indices are invalid when the change does not use that side of
/// the mapping.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly record struct AdvancedGpuRecordPublicationDelta(
    AdvancedGpuHandle Handle,
    EAdvancedGpuRecordPublicationChange Change,
    EAdvancedGpuMutationDomain Domain,
    uint PreviousDenseIndex,
    uint CurrentDenseIndex,
    ulong PublicationGeneration)
{
    public bool RemovesRecord
        => Change == EAdvancedGpuRecordPublicationChange.Tombstoned;
}

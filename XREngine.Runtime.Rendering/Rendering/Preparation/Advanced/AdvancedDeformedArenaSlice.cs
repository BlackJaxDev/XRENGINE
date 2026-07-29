using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Stable current/previous output slice published into deformation and draw
/// records for one frame.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDeformedArenaSlice(
    AdvancedGpuHandle Owner,
    uint CurrentFrameSlot,
    uint PreviousFrameSlot,
    uint CurrentVertexOffset,
    uint PreviousVertexOffset,
    uint VertexCount,
    uint VertexStride,
    uint TopologyGeneration,
    uint LodGeneration,
    EAdvancedVelocityValidityReason VelocityValidity)
{
    public bool HasValidVelocity
        => VelocityValidity == EAdvancedVelocityValidityReason.Valid;

    public ulong CurrentByteOffset
        => (ulong)CurrentVertexOffset * VertexStride;

    public ulong PreviousByteOffset
        => (ulong)PreviousVertexOffset * VertexStride;
}

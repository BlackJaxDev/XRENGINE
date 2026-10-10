using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;

namespace XREngine.Rendering;

/// <summary>Routes one exact draw candidate to a retained physics output page.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedGpuBoundsPatchRoute(
    uint DrawIndex,
    uint DrawGeneration,
    uint CandidateIndex,
    uint BoundsSlot,
    uint SlotGeneration,
    uint ProducerEpoch,
    uint BoneGenerationLow,
    uint BoneGenerationHigh)
{
    public bool IsActive => DrawIndex != 0u && DrawGeneration != 0u;

    internal static AdvancedGpuBoundsPatchRoute Create(
        in AdvancedGpuHandle draw, uint candidateIndex,
        in PhysicsChainGpuBoundsSource source, uint producerEpoch)
        => new(draw.Index, draw.Generation, candidateIndex, source.BoundsSlot,
            source.SlotGeneration, producerEpoch,
            unchecked((uint)source.RendererBoneBufferGeneration),
            unchecked((uint)((ulong)source.RendererBoneBufferGeneration >> 32)));
}

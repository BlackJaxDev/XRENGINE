using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// One compatible visibility indirect range with a GPU-written count.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedIndirectRange(
    AdvancedIndirectRangeKey Key,
    uint FirstPayloadIndex,
    uint PayloadCapacity,
    uint ArgumentBufferOffset,
    uint CountBufferOffset,
    bool CountWrittenByGpu);

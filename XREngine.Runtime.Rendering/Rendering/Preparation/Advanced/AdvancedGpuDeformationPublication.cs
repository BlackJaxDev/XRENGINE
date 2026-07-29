namespace XREngine.Rendering;

/// <summary>
/// GPU resources published for the current shared world preparation. Desktop,
/// eye, shadow, velocity, and reconstruction consumers use the same buffers.
/// </summary>
public readonly record struct AdvancedGpuDeformationPublication(
    ulong FrameId,
    ulong ResourceGeneration,
    uint CurrentFrameSlot,
    uint PreviousFrameSlot,
    XRDataBuffer CurrentVertices,
    XRDataBuffer PreviousVertices,
    XRDataBuffer Jobs,
    XRDataBuffer GroupedJobIndices,
    XRDataBuffer GroupedJobVertexOffsets,
    uint JobCount,
    uint GroupedJobCount,
    bool PreviousOutputValid);

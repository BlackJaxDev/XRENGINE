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
    bool PreviousOutputValid)
{
    /// <summary>Changes on every input publication, including retries in the same world frame.</summary>
    public ulong InputGeneration { get; init; }

    /// <summary>Frozen copy ranges owned by the completion-protected current frame slot.</summary>
    public ReadOnlyMemory<AdvancedGpuDeformationPaletteCopy> GpuPaletteCopies { get; init; }
}

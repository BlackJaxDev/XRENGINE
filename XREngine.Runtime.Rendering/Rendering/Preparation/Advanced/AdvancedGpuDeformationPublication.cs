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

    /// <summary>
    /// The current frame slot's skin palette buffer (three world-space rows per bone; see
    /// <see cref="SkinPaletteMatrix"/>). A visibility payload's <c>paletteOffset</c> indexes it.
    /// The Advanced task stage reads it to bound the posed clusters of skinned cluster-hierarchy
    /// draws. Null when no publication has been made.
    /// </summary>
    public XRDataBuffer? SkinPalettes { get; init; }

    /// <summary>The palette matrices written for this publication; rows past it are stale.</summary>
    public uint SkinPaletteCount { get; init; }
}

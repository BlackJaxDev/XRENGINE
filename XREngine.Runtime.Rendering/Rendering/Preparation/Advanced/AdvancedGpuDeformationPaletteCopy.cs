namespace XREngine.Rendering;

/// <summary>
/// Exact compact-palette byte range and physical source generation captured while
/// the renderer's pose publication is leased. The backend retains that generation
/// when recording its ordered copy; a changed source must never be reconstructed
/// from a CPU mirror.
/// </summary>
public readonly record struct AdvancedGpuDeformationPaletteCopy(
    XRDataBuffer Source,
    AbstractRenderAPIObject SourceOwner,
    nint SourceHandle,
    uint SourceByteLength,
    ulong SourceRevision,
    uint SourceByteOffset,
    uint DestinationByteOffset,
    uint ByteLength)
{
    /// <summary>Identifies the exact live physical source generation independently of its changing pose bytes.</summary>
    public bool IsSourceGenerationCurrent
        => Source is not null && SourceOwner is not null &&
           !Source.IsDestroyed && !Source.IsDestroyQueued && !SourceOwner.IsRetired &&
           SourceOwner.IsGenerated && SourceOwner.GetHandle() == SourceHandle &&
           Source.Length == SourceByteLength &&
           Source.ElementSize == 48 && Source.ComponentType == XREngine.Data.Rendering.EComponentType.Float;

    /// <summary>Also rejects a CPU mutation since the pose was captured for this publication.</summary>
    public bool IsSourceCurrent => IsSourceGenerationCurrent && Source.Revision == SourceRevision;
}

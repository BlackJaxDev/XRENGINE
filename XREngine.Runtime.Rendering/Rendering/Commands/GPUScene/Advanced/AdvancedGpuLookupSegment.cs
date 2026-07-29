namespace XREngine.Rendering.Commands;

/// <summary>
/// Fixed segment in the packed logical-handle lookup buffer.
/// </summary>
public readonly record struct AdvancedGpuLookupSegment(
    uint Offset,
    uint Count)
{
    public uint EndExclusive => checked(Offset + Count);
}

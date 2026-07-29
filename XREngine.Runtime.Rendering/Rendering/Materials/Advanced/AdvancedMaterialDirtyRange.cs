namespace XREngine.Rendering;

/// <summary>
/// Contiguous material-row update range and the generation it publishes.
/// </summary>
public readonly record struct AdvancedMaterialDirtyRange(
    uint FirstRow,
    uint RowCount,
    ulong Generation)
{
    public bool IsEmpty => RowCount == 0u;
}

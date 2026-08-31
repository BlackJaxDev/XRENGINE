namespace XREngine.Rendering.Materials;

/// <summary>
/// Cold diagnostic summary of one material-table publication. Counts describe the exact sparse ranges
/// uploaded for that publication, except a resize which is intentionally represented as one full range.
/// </summary>
public readonly record struct GPUMaterialTablePublicationDelta(
    ulong PublicationGeneration,
    uint MaterialCapacity,
    uint TextureHandleCapacity,
    int MaterialRangeCount,
    uint MaterialRowCount,
    ulong MaterialByteCount,
    int TextureHandleRangeCount,
    uint TextureHandleRowCount,
    ulong TextureHandleByteCount);

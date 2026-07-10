namespace XREngine;

public readonly record struct RvcShadeletDeduplicationPlan(
    bool TileLocalSharedMemoryDedup,
    bool GlobalMergeTileSurvivors,
    bool SortOrBinByMaterial,
    RvcPixelToShadeletEncoding PixelMapEncoding,
    RvcShadeletMapBudget Budget)
{
    public static RvcShadeletDeduplicationPlan Default => new(
        TileLocalSharedMemoryDedup: true,
        GlobalMergeTileSurvivors: true,
        SortOrBinByMaterial: true,
        RvcPixelToShadeletEncoding.Default,
        RvcShadeletMapBudget.Default);
}

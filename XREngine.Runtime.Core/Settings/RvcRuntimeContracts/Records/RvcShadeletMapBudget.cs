namespace XREngine;

public readonly record struct RvcShadeletMapBudget(
    uint MaxShadeletsPerView,
    uint MaxShadeletsPerTile,
    uint MaxMaterialBins,
    uint MaxGlobalDedupSurvivors)
{
    public static RvcShadeletMapBudget Default => new(
        MaxShadeletsPerView: 4_194_304u,
        MaxShadeletsPerTile: 64u,
        MaxMaterialBins: 4096u,
        MaxGlobalDedupSurvivors: 4_194_304u);

    public ERvcFallbackReason Check(uint shadeletsPerView, uint shadeletsPerTile, uint materialBins, uint globalSurvivors)
    {
        if (shadeletsPerView > MaxShadeletsPerView || shadeletsPerTile > MaxShadeletsPerTile)
            return ERvcFallbackReason.ShadeletMapOverflow;
        if (materialBins > MaxMaterialBins || globalSurvivors > MaxGlobalDedupSurvivors)
            return ERvcFallbackReason.DeduplicationOverflow;

        return ERvcFallbackReason.None;
    }
}

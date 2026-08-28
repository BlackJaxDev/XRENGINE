namespace XREngine.Rendering.Models.Caching;

internal sealed record ModelBinaryMeshletSectionPublishResult(
    int PrimaryHydrated,
    int SecondaryHydrated,
    int Repaired,
    IReadOnlyList<ModelBinaryMeshletSectionKey> Unmatched,
    bool RetainedReadOnly,
    string? Warning)
{
    public int TotalHydrated => PrimaryHydrated + SecondaryHydrated + Repaired;
}

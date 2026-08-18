using XREngine.Core.Files.Caching;

namespace XREngine.Rendering.Models.Caching;

internal sealed record ModelBinaryMeshletSectionReadResult(
    ModelBinaryOptionalSectionState State,
    IReadOnlyList<ModelBinaryMeshletSectionEntry>? Entries,
    CacheRejectReason RejectReason,
    string? Detail)
{
    public static ModelBinaryMeshletSectionReadResult Missing { get; } = new(ModelBinaryOptionalSectionState.Missing, null, CacheRejectReason.None, null);
    public static ModelBinaryMeshletSectionReadResult Present(IReadOnlyList<ModelBinaryMeshletSectionEntry> entries)
        => new(ModelBinaryOptionalSectionState.Present, entries, CacheRejectReason.None, null);
    public static ModelBinaryMeshletSectionReadResult Rejected(CacheRejectReason reason, string detail)
        => new(ModelBinaryOptionalSectionState.Rejected, null, reason, detail);
}

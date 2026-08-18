namespace XREngine.Rendering.Models.Caching;

/// <summary>Explicit outcome of attaching an optional meshlet section.</summary>
internal sealed record ModelBinaryMeshletHydrationResult(
    int HydratedCount,
    IReadOnlyList<ModelBinaryMeshletSectionKey> UnmatchedKeys)
{
    public bool HasUnmatched => UnmatchedKeys.Count != 0;
}

using XREngine.Rendering.Commands;

namespace XREngine;

/// <summary>
/// Immutable diagnostic for the most recent resident-template broad
/// invalidation. Broad invalidation is a migration-only correctness fallback;
/// promotion workloads require the count to remain zero.
/// </summary>
public sealed record VulkanResidentTemplateBroadFallbackSnapshot(
    string Reason,
    EBackendReadyCanonicalOwner Owner,
    EBackendTemplateMutationDomain Domain,
    int AffectedEntries,
    ulong PublicationSequence)
{
    public static VulkanResidentTemplateBroadFallbackSnapshot Empty { get; } =
        new(
            string.Empty,
            EBackendReadyCanonicalOwner.None,
            EBackendTemplateMutationDomain.DataContent,
            0,
            0u);
}

using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>Rare migration fallback retained as explicit diagnostic evidence.</summary>
internal readonly record struct VulkanResidentTemplateBroadInvalidationRecord(
    string Reason,
    EBackendReadyCanonicalOwner Owner,
    EBackendTemplateMutationDomain Domain,
    int AffectedEntries,
    ulong PublicationSequence);

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Generation-checked direct address of one resident draw-template variant.
/// The render thread publishes it back to the producer, which carries it in
/// the next request cohort. Staging and recording each validate it with one
/// primary-slot and one variant-slot access.
/// </summary>
internal readonly record struct VulkanResidentDrawTemplateHandle(
    uint PrimaryIndex,
    uint CanonicalHandleGeneration,
    ulong DatabaseEpoch,
    ushort VariantOrdinal,
    uint EntryGeneration)
{
    internal bool IsValid =>
        PrimaryIndex != 0u &&
        CanonicalHandleGeneration != 0u &&
        DatabaseEpoch != 0u &&
        EntryGeneration != 0u;
}

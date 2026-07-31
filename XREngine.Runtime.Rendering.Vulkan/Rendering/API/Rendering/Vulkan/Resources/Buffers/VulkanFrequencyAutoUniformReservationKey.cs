namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one frequency-owned auto-uniform range within the renderer's
/// persistent frame-data arena.
/// </summary>
internal readonly record struct VulkanFrequencyAutoUniformReservationKey(
    ulong PublicationLayoutSignature,
    EVulkanBindingFrequency Frequency,
    ulong OwnerIdentity);

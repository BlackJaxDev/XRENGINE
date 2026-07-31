namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one frequency-owned physical publication layout. Compatible
/// linked programs collapse to the same work item, while distinct owners or
/// content generations remain separate.
/// </summary>
internal readonly record struct VulkanReusableFrameOwnerKey(
    ulong PublicationLayoutSignature,
    EVulkanBindingFrequency Frequency,
    ulong OwnerIdentity,
    ulong ContentGeneration);

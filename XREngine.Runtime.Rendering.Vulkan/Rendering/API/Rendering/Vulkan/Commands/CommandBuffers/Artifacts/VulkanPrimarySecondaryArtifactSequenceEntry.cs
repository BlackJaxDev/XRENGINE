namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one command-chain secondary artifact in the order encoded by a
/// primary command buffer.
/// </summary>
internal readonly record struct VulkanPrimarySecondaryArtifactSequenceEntry(
    CommandChainKey Key,
    VulkanRecordedCommandArtifactReference Artifact);

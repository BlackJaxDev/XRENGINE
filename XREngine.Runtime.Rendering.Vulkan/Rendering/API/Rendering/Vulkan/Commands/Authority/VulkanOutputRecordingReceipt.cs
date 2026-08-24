using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable output-facing receipt for a recorded primary. It deliberately
/// contains native identities only; output ownership remains in the frame loop.
/// </summary>
internal readonly record struct VulkanOutputRecordingReceipt(
    CommandBuffer PrimaryCommandBuffer,
    ImageLayout SwapchainLayoutAfterCommandBuffer,
    int SwapchainWriteCount,
    VulkanPresentationSourceTuple PresentationSource,
    long CommandBufferDirtyGeneration);

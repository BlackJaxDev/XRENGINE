using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact physical identity of one recorded render-target attachment.
/// </summary>
internal readonly record struct VulkanNativeAttachmentIdentity(
    ulong ImageHandle,
    ulong ImageGeneration,
    ulong ImageViewHandle,
    ulong ImageViewGeneration,
    ImageLayout ExpectedLayout)
{
    public bool IsComplete =>
        ImageHandle != 0UL &&
        ImageGeneration != 0UL &&
        ImageViewHandle != 0UL &&
        ImageViewGeneration != 0UL;
}

using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Describes one exact image-subresource entry-state mismatch without
/// allocating diagnostic text in the command-buffer reuse hot path.
/// </summary>
internal readonly record struct VulkanImageEntryStateMismatch(
    EVulkanPrimaryEntryStateMismatch Kind,
    ulong ImageHandle,
    uint MipLevel,
    uint ArrayLayer,
    ImageAspectFlags Aspect,
    VulkanRenderer.VulkanImageAccessState Expected,
    VulkanRenderer.VulkanImageAccessState Actual)
{
    public bool RequiresRecording => Kind != EVulkanPrimaryEntryStateMismatch.None;
}

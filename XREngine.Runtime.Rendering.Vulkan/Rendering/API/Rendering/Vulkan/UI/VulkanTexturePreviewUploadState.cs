namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracks the exact asynchronous upload ticket for one renderer-local texture
/// preview request. The weak-key owner lives in the Vulkan frame loop.
/// </summary>
internal sealed class VulkanTexturePreviewUploadState(
    VulkanTextureStreamingUploadTicket ticket)
{
    internal VulkanTextureStreamingUploadTicket Ticket { get; } = ticket;
}

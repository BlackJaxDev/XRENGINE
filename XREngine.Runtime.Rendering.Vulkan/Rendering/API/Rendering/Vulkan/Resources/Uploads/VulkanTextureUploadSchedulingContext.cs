namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen authority inputs for one imported-texture upload scheduling sequence.
/// The upload service retains this concrete request rather than a renderer facade.
/// </summary>
internal readonly record struct VulkanTextureUploadSchedulingContext(
    VulkanBackendObjectContext BackendObjects,
    VulkanResourceRuntime Resources,
    VulkanCommandRuntime Commands,
    VulkanFrameOperationQueue Operations,
    FrameOpContext FrameContext)
{
    internal bool IsDeviceOperational => BackendObjects.IsDeviceOperational;
}

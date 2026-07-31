namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frequency and content generation declared by the publisher that wrote one
/// runtime uniform into a private binding capture.
/// </summary>
internal readonly record struct VulkanRuntimeUniformPublication(
    EVulkanBindingFrequency Frequency,
    ulong Generation);

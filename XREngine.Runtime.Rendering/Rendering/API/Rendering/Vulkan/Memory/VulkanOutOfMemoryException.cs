using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Thrown when a Vulkan memory allocation fails due to device or host memory exhaustion.
/// Contains the requested properties so callers can attempt fallback.
/// </summary>
internal sealed class VulkanOutOfMemoryException : Exception
{
    public MemoryPropertyFlags RequestedProperties { get; }

    public VulkanOutOfMemoryException(string message, MemoryPropertyFlags requestedProperties)
        : base(message)
    {
        RequestedProperties = requestedProperties;
    }

    public VulkanOutOfMemoryException(string message, MemoryPropertyFlags requestedProperties, Exception innerException)
        : base(message, innerException)
    {
        RequestedProperties = requestedProperties;
    }
}

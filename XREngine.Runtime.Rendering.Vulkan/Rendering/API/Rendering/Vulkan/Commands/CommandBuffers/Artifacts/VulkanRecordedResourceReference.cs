using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact native resource generation retained by a recorded command artifact.
/// </summary>
internal readonly record struct VulkanRecordedResourceReference(
    ObjectType Type,
    ulong Handle,
    ulong Generation);

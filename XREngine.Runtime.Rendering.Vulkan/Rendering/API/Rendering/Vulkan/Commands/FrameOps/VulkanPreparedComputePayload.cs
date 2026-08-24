using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frame-local descriptor selection frozen after compute binding preparation.
/// The binding path already owns the returned array exclusively; retaining that
/// array avoids an additional hot-path clone and keeps the sealed frame op clean.
/// </summary>
internal readonly record struct VulkanPreparedComputePayload(DescriptorSet[] DescriptorSets);

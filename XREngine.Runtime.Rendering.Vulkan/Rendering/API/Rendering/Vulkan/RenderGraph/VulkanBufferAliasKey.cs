using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanBufferAliasKey(
    ulong SizeInBytes,
    EBufferTarget Target,
    EBufferUsage Usage);

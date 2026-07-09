using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanDeviceAddressRange(
        Buffer Buffer,
        ulong BaseAddress,
        ulong Size,
        string? Label,
        bool Active);
}

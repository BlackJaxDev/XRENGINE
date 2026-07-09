using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanDeviceAddressBindingEvent(
        ulong Serial,
        ulong BaseAddress,
        ulong Size,
        DeviceAddressBindingTypeEXT BindingType,
        DeviceAddressBindingFlagsEXT Flags,
        string? CorrelatedObject);
}

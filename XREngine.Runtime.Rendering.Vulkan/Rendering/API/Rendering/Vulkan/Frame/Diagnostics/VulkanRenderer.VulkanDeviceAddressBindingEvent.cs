using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanDeviceAddressBindingEvent(
    ulong Serial,
    ulong BaseAddress,
    ulong Size,
    DeviceAddressBindingTypeEXT BindingType,
    DeviceAddressBindingFlagsEXT Flags,
    string? CorrelatedObject);

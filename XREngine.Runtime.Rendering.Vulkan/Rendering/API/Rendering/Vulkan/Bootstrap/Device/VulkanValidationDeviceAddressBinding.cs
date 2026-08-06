using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer-independent copy of a device-address binding callback payload.
/// </summary>
internal readonly record struct VulkanValidationDeviceAddressBinding(
    ulong BaseAddress,
    ulong Size,
    DeviceAddressBindingTypeEXT BindingType,
    DeviceAddressBindingFlagsEXT Flags);

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable first native operation that reported logical-device loss.
/// </summary>
internal sealed record VulkanNativeDeviceFault(
    string Operation,
    Result Result);

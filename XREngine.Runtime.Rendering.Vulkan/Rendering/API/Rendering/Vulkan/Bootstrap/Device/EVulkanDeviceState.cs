namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Terminal lifecycle states for one Vulkan logical-device lifetime.
/// </summary>
internal enum EVulkanDeviceState : byte
{
    Healthy,
    LossDetected,
    CollectingFaultData,
    Quiesced,
    Disposed,
}

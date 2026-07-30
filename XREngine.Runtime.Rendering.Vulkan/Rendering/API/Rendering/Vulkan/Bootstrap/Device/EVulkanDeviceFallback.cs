namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

[Flags]
internal enum EVulkanDeviceFallback : byte
{
    None = 0,
    SingleGraphicsQueue = 1 << 0,
    LegacySynchronization = 1 << 1,
    LegacyRenderPass = 1 << 2,
    ClassicDescriptors = 1 << 3,
    DeviceFaultDiagnosticsUnavailable = 1 << 4,
}

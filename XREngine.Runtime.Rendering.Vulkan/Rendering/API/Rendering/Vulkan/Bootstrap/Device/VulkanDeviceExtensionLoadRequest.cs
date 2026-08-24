namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable post-create command-loading policy. The composition root resolves
/// integration-specific command alias requirements before handing control to
/// the native device authority.
/// </summary>
internal readonly record struct VulkanDeviceExtensionLoadRequest(
    bool RequireKhrDynamicRenderingCommands,
    bool RequireKhrSynchronization2Commands,
    bool EnableCoreDrawIndirectCount);

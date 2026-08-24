namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable outcome of native instance creation for composition-root
/// projections such as renderer statistics.
/// </summary>
internal readonly record struct VulkanDeviceBootstrapResult(
    bool ValidationLayersEnabled,
    bool SynchronizationValidationEnabled);

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable renderer policy inputs used to publish the shared layered-shadow
/// planner capability contract from Vulkan device facts.
/// </summary>
internal readonly record struct VulkanLayeredShadowCapabilityRequest(
    bool EnableMultiViewport,
    bool EnableShaderOutputViewportIndex,
    bool EnableShaderOutputLayer);

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable non-device runtime facts needed to render the Vulkan startup
/// capability report. Native device facts remain sourced from the device context.
/// </summary>
internal readonly record struct VulkanStartupCapabilitySnapshot(
    EVulkanCapabilityTier RequestedCapabilityTier,
    EVulkanDescriptorBackend RequestedDescriptorBackend,
    EVulkanDescriptorBackend ActiveDescriptorBackend,
    EVulkanProgramBindingBackend RequestedProgramBindingBackend,
    EVulkanFoveationBackend RequestedFoveationBackend,
    EVulkanRayTracingBackend RequestedRayTracingBackend,
    EVulkanRenderTargetMode RequestedRenderTargetMode,
    bool UseDynamicRenderingRenderTargets,
    EVulkanSynchronizationBackend RequestedSynchronizationBackend,
    EVulkanSynchronizationBackend ActiveSynchronizationBackend,
    EVulkanGeometryFetchMode ActiveGeometryFetchMode,
    bool EnableBindlessMaterialTable,
    bool DescriptorIndexingSupported,
    VulkanBindlessMaterialCapability BindlessMaterialCapability,
    bool DescriptorHeapNativeApiAvailable,
    bool DescriptorHeapStorageReady,
    bool DescriptorHeapShaderUntypedPointersAvailable,
    string DescriptorHeapProperties,
    string DescriptorBackendFallbackReason,
    bool HasVulkanMultiView,
    bool SupportsVertexShaderLayeredRendering);

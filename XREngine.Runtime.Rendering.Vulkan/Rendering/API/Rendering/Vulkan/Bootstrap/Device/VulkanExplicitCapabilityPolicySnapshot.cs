namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable device, backend, and explicit-request facts consumed by Vulkan
/// capability admission policy after logical-device creation.
/// </summary>
internal readonly record struct VulkanExplicitCapabilityPolicySnapshot(
    bool Vulkan13PromotedToCore,
    bool Vulkan14PromotedToCore,
    bool DynamicRenderingEnabled,
    bool DynamicRenderingLocalReadEnabled,
    bool Maintenance4Enabled,
    bool Maintenance5Enabled,
    bool Synchronization2Enabled,
    bool TimelineSemaphoreEnabled,
    bool DescriptorIndexingEnabled,
    bool BufferDeviceAddressEnabled,
    bool DrawIndirectCountEnabled,
    bool DescriptorHeapExtensionAvailable,
    bool DescriptorHeapDependenciesReady,
    bool DescriptorHeapNativeApiAvailable,
    bool SupportsDescriptorHeap,
    EVulkanDescriptorBackend ActiveDescriptorBackend,
    string DescriptorBackendFallbackReason,
    bool ShaderObjectFeatureSupported,
    bool FragmentShadingRateSupported,
    bool FragmentDensityMapSupported,
    bool AccelerationStructureSupported,
    bool RayTracingPipelineSupported,
    bool RayQuerySupported,
    EVulkanCapabilityTier? ExplicitCapabilityTier,
    EVulkanDescriptorBackend? ExplicitDescriptorBackend,
    EVulkanProgramBindingBackend? ExplicitProgramBindingBackend,
    EVulkanFoveationBackend? ExplicitFoveationBackend,
    EVulkanRayTracingBackend? ExplicitRayTracingBackend);

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>Device-owned mesh-shader capability policy.</summary>
internal sealed partial class VulkanDeviceContext
{
    internal bool SupportsMeshTaskIndirectCount
        => Capabilities.Supports(EVulkanDeviceCapability.MeshShader)
            && MutableCapabilities._supportsVulkanMeshShaderFeature
            && MutableCapabilities._supportsVulkanMeshTaskIndirectCount
            && ExtensionFunctions.ExtMeshShader is not null;

    internal ERvcVulkanProductionFeature ResolveRvcProductionFeatures(bool multiview)
    {
        ERvcVulkanProductionFeature features = ERvcVulkanProductionFeature.None;
        Add(ERvcVulkanProductionFeature.Multiview, multiview);
        Add(ERvcVulkanProductionFeature.DynamicRendering, Capabilities.Supports(EVulkanDeviceCapability.DynamicRendering));
        Add(ERvcVulkanProductionFeature.Synchronization2, Capabilities.Supports(EVulkanDeviceCapability.Synchronization2));
        Add(ERvcVulkanProductionFeature.DescriptorIndexing, Capabilities.Supports(EVulkanDeviceCapability.DescriptorIndexing));
        Add(ERvcVulkanProductionFeature.FragmentShadingRate, Capabilities.Supports(EVulkanDeviceCapability.FragmentShadingRate));
        Add(ERvcVulkanProductionFeature.FragmentDensityMap, Capabilities.Supports(EVulkanDeviceCapability.FragmentDensityMap));
        Add(ERvcVulkanProductionFeature.MeshShader, SupportsMeshTaskIndirectCount);
        Add(ERvcVulkanProductionFeature.TimelineSemaphore, Capabilities.Supports(EVulkanDeviceCapability.TimelineSemaphores));
        return features;

        void Add(ERvcVulkanProductionFeature feature, bool supported)
        {
            if (supported)
                features |= feature;
        }
    }
}

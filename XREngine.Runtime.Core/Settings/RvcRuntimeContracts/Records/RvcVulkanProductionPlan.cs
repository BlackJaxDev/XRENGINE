namespace XREngine;

public readonly record struct RvcVulkanProductionPlan(
    ERvcVulkanProductionFeature RequiredFeatures,
    ERvcVulkanProductionFeature AvailableFeatures,
    bool OpenGlCorrectnessSliceOnly,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public ERvcVulkanProductionFeature MissingFeatures => RequiredFeatures & ~AvailableFeatures;
    public bool IsProductionReady => !OpenGlCorrectnessSliceOnly && MissingFeatures == ERvcVulkanProductionFeature.None;

    public static RvcVulkanProductionPlan Resolve(
        in RvcPipelineResolution resolution,
        in RvcCapabilityMatrix capabilities,
        bool dynamicRenderingAvailable,
        bool synchronization2Available,
        bool meshShaderAvailable,
        bool timelineSemaphoreAvailable)
    {
        ERvcVulkanProductionFeature required = ERvcVulkanProductionFeature.None;
        if (resolution.EffectiveMode == ERvcPipelineMode.Full)
        {
            required =
                ERvcVulkanProductionFeature.DynamicRendering |
                ERvcVulkanProductionFeature.Synchronization2 |
                ERvcVulkanProductionFeature.DescriptorIndexing |
                ERvcVulkanProductionFeature.TimelineSemaphore;

            if (capabilities.MultiviewSupported)
                required |= ERvcVulkanProductionFeature.Multiview;
            if (capabilities.FragmentShadingRateSupported)
                required |= ERvcVulkanProductionFeature.FragmentShadingRate;
            if (capabilities.FragmentDensityMapSupported)
                required |= ERvcVulkanProductionFeature.FragmentDensityMap;
            if (meshShaderAvailable)
                required |= ERvcVulkanProductionFeature.MeshShader;
        }

        ERvcVulkanProductionFeature available = ERvcVulkanProductionFeature.None;
        if (capabilities.MultiviewSupported)
            available |= ERvcVulkanProductionFeature.Multiview;
        if (dynamicRenderingAvailable)
            available |= ERvcVulkanProductionFeature.DynamicRendering;
        if (synchronization2Available)
            available |= ERvcVulkanProductionFeature.Synchronization2;
        if (capabilities.DescriptorIndexingSupported)
            available |= ERvcVulkanProductionFeature.DescriptorIndexing;
        if (capabilities.FragmentShadingRateSupported)
            available |= ERvcVulkanProductionFeature.FragmentShadingRate;
        if (capabilities.FragmentDensityMapSupported)
            available |= ERvcVulkanProductionFeature.FragmentDensityMap;
        if (meshShaderAvailable)
            available |= ERvcVulkanProductionFeature.MeshShader;
        if (timelineSemaphoreAvailable)
            available |= ERvcVulkanProductionFeature.TimelineSemaphore;

        bool openGlSlice = capabilities.OpenGlBackend && !capabilities.VulkanBackend;
        ERvcVulkanProductionFeature missing = required & ~available;
        return new(
            required,
            available,
            openGlSlice,
            missing == ERvcVulkanProductionFeature.None ? ERvcFallbackReason.None : ERvcFallbackReason.MissingVulkanProductionFeature,
            missing == ERvcVulkanProductionFeature.None
                ? "RVC Vulkan production feature plan is satisfied by the declared capabilities."
                : "RVC Vulkan production feature plan is missing one or more required features.");
    }
}

namespace XREngine;

public readonly record struct RvcCapabilityMatrix(
    bool ForwardPlusOracleAvailable,
    bool FrameGraphAvailable,
    bool VisibilityTargetsAvailable,
    bool VulkanBackend,
    bool OpenGlBackend,
    bool DescriptorHeapSupported,
    bool DescriptorIndexingSupported,
    bool FragmentShadingRateSupported,
    bool FragmentDensityMapSupported,
    bool OpenXrQuadViewsSupported,
    bool OpenXrRuntimeFoveationSupported,
    bool OpenXrDepthLayersSupported,
    bool OpenXrVisibilityMaskSupported,
    bool MultiviewSupported,
    bool StaticMeshVisibilitySourceSupported = false,
    bool SkinnedComputeVisibilitySourceSupported = false,
    bool ZeroReadbackIndirectVisibilitySourceSupported = false,
    bool MeshletVisibilitySourceSupported = false,
    bool VulkanDynamicRenderingSupported = false,
    bool VulkanSynchronization2Supported = false,
    bool VulkanMeshShaderSupported = false,
    bool VulkanTimelineSemaphoreSupported = false)
{
    public ERvcDescriptorBackend ResolveDescriptorBackend()
    {
        if (DescriptorHeapSupported)
            return ERvcDescriptorBackend.DescriptorHeap;
        if (DescriptorIndexingSupported)
            return ERvcDescriptorBackend.DescriptorIndexing;
        return ERvcDescriptorBackend.None;
    }

    public ERvcFoveationRateBackend ResolveFoveationRateBackend()
    {
        if (FragmentShadingRateSupported)
            return ERvcFoveationRateBackend.VulkanFragmentShadingRate;
        if (FragmentDensityMapSupported)
            return ERvcFoveationRateBackend.VulkanFragmentDensityMap;
        if (OpenXrQuadViewsSupported)
            return ERvcFoveationRateBackend.OpenXrQuadViews;
        if (OpenXrRuntimeFoveationSupported)
            return ERvcFoveationRateBackend.OpenXrRuntimeFoveation;
        return ERvcFoveationRateBackend.None;
    }

    public static RvcCapabilityMatrix ForwardPlusOnly(bool vulkanBackend = false, bool openGlBackend = false)
        => new(
            ForwardPlusOracleAvailable: true,
            FrameGraphAvailable: true,
            VisibilityTargetsAvailable: false,
            VulkanBackend: vulkanBackend,
            OpenGlBackend: openGlBackend,
            DescriptorHeapSupported: false,
            DescriptorIndexingSupported: vulkanBackend,
            FragmentShadingRateSupported: false,
            FragmentDensityMapSupported: false,
            OpenXrQuadViewsSupported: false,
            OpenXrRuntimeFoveationSupported: false,
            OpenXrDepthLayersSupported: false,
            OpenXrVisibilityMaskSupported: false,
            MultiviewSupported: false);
}

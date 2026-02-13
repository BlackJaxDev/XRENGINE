namespace XREngine.Rendering.Vulkan;

public enum EVulkanGpuDrivenProfile
{
    Auto = 0,
    ShippingFast,
    DevParity,
    Diagnostics,
}

/// <summary>
/// Centralised feature-gate queries for the Vulkan CPU-octree render path.
/// Pipelines and render commands should consult this profile to decide whether
/// compute-dependent passes, GPU dispatch, and other Vulkan-sensitive features
/// are safe to enable at the current point in the backend bring-up.
/// </summary>
public static class VulkanFeatureProfile
{
    /// <summary>
    /// Returns <c>true</c> when the Vulkan renderer is the currently active backend
    /// and the safe feature profile should be used to restrict unsupported features.
    /// </summary>
    public static bool IsActive
        => Engine.Rendering.IsVulkanRendererActive();

    public static EVulkanGpuDrivenProfile ActiveProfile
        => ResolveRuntimeProfile(
            Engine.EffectiveSettings.VulkanGpuDrivenProfile,
            Engine.GameSettings?.BuildSettings?.Configuration ?? EBuildConfiguration.Development);

    public static EVulkanGpuDrivenProfile ResolveRuntimeProfile(EVulkanGpuDrivenProfile configuredProfile, EBuildConfiguration buildConfiguration)
    {
        if (configuredProfile != EVulkanGpuDrivenProfile.Auto)
            return configuredProfile;

        return buildConfiguration == EBuildConfiguration.Debug
            ? EVulkanGpuDrivenProfile.DevParity
            : EVulkanGpuDrivenProfile.ShippingFast;
    }

    private static bool ProfileAllowsComputeDependentPasses
        => ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => true,
            EVulkanGpuDrivenProfile.DevParity => true,
            EVulkanGpuDrivenProfile.Diagnostics => true,
            _ => false,
        };

    private static bool ProfileAllowsGpuRenderDispatch
        => ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => true,
            EVulkanGpuDrivenProfile.DevParity => true,
            EVulkanGpuDrivenProfile.Diagnostics => true,
            _ => false,
        };

    private static bool ProfileAllowsGpuBvh
        => ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => true,
            EVulkanGpuDrivenProfile.DevParity => true,
            EVulkanGpuDrivenProfile.Diagnostics => true,
            _ => false,
        };

    private static bool ProfileAllowsOcclusion
        => ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => true,
            EVulkanGpuDrivenProfile.DevParity => true,
            EVulkanGpuDrivenProfile.Diagnostics => true,
            _ => false,
        };

    private static bool ProfileAllowsImGui
        => ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => false,
            EVulkanGpuDrivenProfile.DevParity => false,
            EVulkanGpuDrivenProfile.Diagnostics => false,
            _ => false,
        };

    private static bool ProfileAllowsDescriptorIndexing
        => ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => true,
            EVulkanGpuDrivenProfile.DevParity => true,
            EVulkanGpuDrivenProfile.Diagnostics => true,
            _ => false,
        };

    private static bool ProfileAllowsBindlessMaterialTable
        => ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => true,
            EVulkanGpuDrivenProfile.DevParity => true,
            EVulkanGpuDrivenProfile.Diagnostics => true,
            _ => false,
        };

    private static bool ProfileAllowsDescriptorContractValidation
        => ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => false,
            EVulkanGpuDrivenProfile.DevParity => true,
            EVulkanGpuDrivenProfile.Diagnostics => true,
            _ => false,
        };

    private static bool ProfileAllowsRtxIoVulkanDecompression
        => ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => true,
            EVulkanGpuDrivenProfile.DevParity => true,
            EVulkanGpuDrivenProfile.Diagnostics => true,
            _ => false,
        };

    private static bool ProfileAllowsRtxIoVulkanCopyMemoryIndirect
        => ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => true,
            EVulkanGpuDrivenProfile.DevParity => true,
            EVulkanGpuDrivenProfile.Diagnostics => true,
            _ => false,
        };

    public static bool ResolveComputeDependentPassesPreference(bool requested)
    {
        if (!requested)
            return false;

        return !IsActive || ProfileAllowsComputeDependentPasses;
    }

    public static bool ResolveGpuRenderDispatchPreference(bool requested)
    {
        if (!requested)
            return false;

        return !IsActive || ProfileAllowsGpuRenderDispatch;
    }

    public static bool ResolveGpuBvhPreference(bool requested)
    {
        if (!requested)
            return false;

        return !IsActive || ProfileAllowsGpuBvh;
    }

    public static bool ResolveImGuiPreference(bool requested)
    {
        if (!requested)
            return false;

        return !IsActive || ProfileAllowsImGui;
    }

    public static bool ResolveDescriptorIndexingPreference(bool requested)
    {
        if (!requested)
            return false;

        return !IsActive || ProfileAllowsDescriptorIndexing;
    }

    public static bool ResolveBindlessMaterialTablePreference(bool requested)
    {
        if (!requested)
            return false;

        return !IsActive || ProfileAllowsBindlessMaterialTable;
    }

    public static bool ResolveDescriptorContractValidationPreference(bool requested)
    {
        if (!requested)
            return false;

        return !IsActive || ProfileAllowsDescriptorContractValidation;
    }

    public static bool ResolveRtxIoVulkanDecompressionPreference(bool requested)
    {
        if (!requested)
            return false;

        return !IsActive || ProfileAllowsRtxIoVulkanDecompression;
    }

    public static bool ResolveRtxIoVulkanCopyMemoryIndirectPreference(bool requested)
    {
        if (!requested)
            return false;

        return !IsActive || ProfileAllowsRtxIoVulkanCopyMemoryIndirect;
    }

    public static EVulkanGeometryFetchMode ResolveGeometryFetchMode(EVulkanGeometryFetchMode requested)
    {
        if (!IsActive)
            return requested;

        if (requested == EVulkanGeometryFetchMode.BufferDeviceAddressPrototype && ActiveProfile != EVulkanGpuDrivenProfile.Diagnostics)
            return EVulkanGeometryFetchMode.Atlas;

        return requested;
    }

    public static EOcclusionCullingMode ResolveOcclusionCullingMode(EOcclusionCullingMode requested)
    {
        if (requested == EOcclusionCullingMode.Disabled)
            return requested;

        return (!IsActive || ProfileAllowsOcclusion)
            ? requested
            : EOcclusionCullingMode.Disabled;
    }

    /// <summary>
    /// Compute-dependent render passes (Forward+ light culling, ReSTIR GI, Surfel GI,
    /// radiance cascades, voxel cone tracing, spatial-hash AO, etc.) require fully wired
    /// compute dispatch + descriptor set support.  Returns <c>false</c> when the Vulkan
    /// backend is active and these systems are not yet verified.
    /// </summary>
    public static bool EnableComputeDependentPasses
        => ResolveComputeDependentPassesPreference(true);

    /// <summary>
    /// GPU-driven render dispatch (GPU culling, indirect draws) requires compute + buffer
    /// infrastructure that is still being hardened on Vulkan.  Returns <c>false</c> when
    /// the Vulkan backend is active.
    /// </summary>
    public static bool EnableGpuRenderDispatch
        => ResolveGpuRenderDispatchPreference(true);

    /// <summary>
    /// GPU BVH raycast dispatch.  Returns <c>false</c> when the Vulkan backend is active.
    /// </summary>
    public static bool EnableGpuBvh
        => ResolveGpuBvhPreference(true);

    /// <summary>
    /// ImGui rendering through the Vulkan pipeline.  Currently disabled on Vulkan
    /// (<c>SupportsImGui == false</c>).
    /// </summary>
    public static bool EnableImGui
        => ResolveImGuiPreference(true);

    public static bool EnableDescriptorIndexing
        => ResolveDescriptorIndexingPreference(Engine.EffectiveSettings.EnableVulkanDescriptorIndexing);

    public static bool EnableBindlessMaterialTable
        => ResolveBindlessMaterialTablePreference(Engine.EffectiveSettings.EnableVulkanBindlessMaterialTable);

    public static bool EnableDescriptorContractValidation
        => ResolveDescriptorContractValidationPreference(Engine.EffectiveSettings.ValidateVulkanDescriptorContracts);

    public static bool EnableRtxIoVulkanDecompression
        => ResolveRtxIoVulkanDecompressionPreference(true);

    public static bool EnableRtxIoVulkanCopyMemoryIndirect
        => ResolveRtxIoVulkanCopyMemoryIndirectPreference(true);

    public static EVulkanGeometryFetchMode ActiveGeometryFetchMode
        => ResolveGeometryFetchMode(Engine.EffectiveSettings.VulkanGeometryFetchMode);
}

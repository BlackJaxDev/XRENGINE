using System;

namespace XREngine.Rendering.Vulkan;

public enum EVulkanGpuDrivenProfile
{
    Auto = 0,
    ShippingFast,
    DevParity,
    Diagnostics,
}

public enum EVulkanQueueOverlapMode
{
    Auto = 0,
    GraphicsOnly,
    GraphicsCompute,
    GraphicsComputeTransfer,
}

public enum EVulkanBindlessMaterialMode
{
    Auto = 0,
    Disabled,
    Required,
    Diagnostics,
}

[Flags]
public enum EVulkanDiagnosticFlags
{
    None = 0,
    StandardValidation = 1 << 0,
    SynchronizationValidation = 1 << 1,
    GpuAssistedValidation = 1 << 2,
    BestPractices = 1 << 3,
    DebugUtils = 1 << 4,
    CommandBufferLabels = 1 << 5,
    CrashBreadcrumbs = 1 << 6,
    DeviceFault = 1 << 7,
    DeviceAddressBindingReport = 1 << 8,
    NvDiagnosticCheckpoints = 1 << 9,
    NvDiagnosticsConfig = 1 << 10,
    RenderDocFriendly = 1 << 11,
    DeviceFaultDeviceLostOnMasked = 1 << 12,
}

public enum EVulkanDiagnosticPreset
{
    Off = 0,
    StandardValidation,
    SyncValidation,
    GpuAssisted,
    BestPractices,
    CrashDiagnostics,
    RenderDocFriendly,
}

public enum EVulkanBindlessMaterialCapabilityTier
{
    DescriptorIndexingUnavailable = 0,
    DescriptorIndexingReady,
    GlobalMaterialTextureTableReady,
    BindlessMaterialTableShaderReady,
    BindlessMaterialDrawPathReady,
}

public enum EVulkanCapabilityTier
{
    Vulkan13Production = 0,
    Vulkan14OptInBaseline,
    Vulkan14Experimental,
}

public enum EVulkanDescriptorBackend
{
    DescriptorSets = 0,
    DescriptorIndexing,
    DescriptorHeap,
}

public enum EVulkanProgramBindingBackend
{
    PipelineObjects = 0,
    ShaderObjects,
}

public enum EVulkanFoveationBackend
{
    Off = 0,
    FragmentShadingRate,
    FragmentDensityMap,
}

public enum EVulkanRayTracingBackend
{
    Off = 0,
    RayTracingPipeline,
    RayQuery,
}

public enum EVulkanCapabilityState
{
    Unavailable = 0,
    AvailableDisabled,
    EnabledUnused,
    EnabledActive,
    ExplicitlyRequiredMissing,
}

public readonly record struct VulkanBindlessMaterialCapability(
    EVulkanBindlessMaterialMode Mode,
    EVulkanBindlessMaterialCapabilityTier Tier,
    bool DescriptorIndexing,
    bool RuntimeDescriptorArray,
    bool PartiallyBound,
    bool UpdateAfterBind,
    uint DescriptorCapacity,
    bool GlobalDescriptorTableReady,
    bool ShaderReady,
    bool DrawPathReady,
    string Reason);

/// <summary>
/// Centralised feature-gate queries for the Vulkan CPU-octree render path.
/// Pipelines and render commands should consult this profile to decide whether
/// compute-dependent passes, GPU dispatch, and other Vulkan-sensitive features
/// are safe to enable at the current point in the backend bring-up.
/// </summary>
public static class VulkanFeatureProfile
{
    public const string BindlessMaterialModeEnvVar = XREngineEnvironmentVariables.VulkanBindlessMaterialMode;
    public const string CapabilityTierEnvVar = XREngineEnvironmentVariables.VkCapabilityTier;
    public const string DescriptorBackendEnvVar = XREngineEnvironmentVariables.VkDescriptorBackend;
    public const string ProgramBindingBackendEnvVar = XREngineEnvironmentVariables.VkProgramBindingBackend;
    public const string FoveationBackendEnvVar = XREngineEnvironmentVariables.VkFoveationBackend;
    public const string RayTracingBackendEnvVar = XREngineEnvironmentVariables.VkRayTracingBackend;
    /// <summary>
    /// Returns <c>true</c> when the Vulkan renderer is the currently active backend
    /// and the safe feature profile should be used to restrict unsupported features.
    /// </summary>
    public static bool IsActive
        => RuntimeEngine.Rendering.IsVulkanRendererActive();

    public static EVulkanGpuDrivenProfile ActiveProfile
        => ResolveRuntimeProfile(
            RuntimeEngine.EffectiveSettings.VulkanGpuDrivenProfile,
            RuntimeEngine.GameSettings?.BuildSettings?.Configuration ?? EBuildConfiguration.Development);

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
            EVulkanGpuDrivenProfile.ShippingFast => true,
            EVulkanGpuDrivenProfile.DevParity => true,
            EVulkanGpuDrivenProfile.Diagnostics => true,
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

    /// <summary>
    /// Returns whether the selected submission strategy should use GPU BVH culling.
    /// Runtime shader and BVH-resource readiness is checked by the culling pass itself.
    /// </summary>
    public static bool ResolveGpuBvhUsage(EMeshSubmissionStrategy strategy)
    {
        if (!strategy.UsesGpuBvhCulling())
            return false;

        if (!IsActive)
            return true;

        return ProfileAllowsGpuBvh;
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

    public static EVulkanBindlessMaterialMode ResolveBindlessMaterialMode(
        EVulkanBindlessMaterialMode configuredMode,
        bool legacyEnabled)
    {
        if (TryGetBindlessMaterialModeEnvOverride(out EVulkanBindlessMaterialMode envMode))
            return envMode;

        if (!legacyEnabled)
            return EVulkanBindlessMaterialMode.Disabled;

        return configuredMode;
    }

    private static bool TryGetBindlessMaterialModeEnvOverride(out EVulkanBindlessMaterialMode mode)
    {
        return TryGetEnumEnvOverride(BindlessMaterialModeEnvVar, out mode);
    }

    public static bool TryGetCapabilityTierEnvOverride(out EVulkanCapabilityTier tier)
        => TryGetEnumEnvOverride(CapabilityTierEnvVar, out tier);

    public static bool TryGetDescriptorBackendEnvOverride(out EVulkanDescriptorBackend backend)
        => TryGetEnumEnvOverride(DescriptorBackendEnvVar, out backend);

    public static bool TryGetProgramBindingBackendEnvOverride(out EVulkanProgramBindingBackend backend)
        => TryGetEnumEnvOverride(ProgramBindingBackendEnvVar, out backend);

    public static bool TryGetFoveationBackendEnvOverride(out EVulkanFoveationBackend backend)
        => TryGetEnumEnvOverride(FoveationBackendEnvVar, out backend);

    public static bool TryGetRayTracingBackendEnvOverride(out EVulkanRayTracingBackend backend)
        => TryGetEnumEnvOverride(RayTracingBackendEnvVar, out backend);

    private static bool TryGetEnumEnvOverride<TEnum>(string environmentVariable, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        string? raw = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return Enum.TryParse(raw.Trim(), ignoreCase: true, out value);
    }

    private static bool TryParseEnabled(string? raw, out bool enabled)
    {
        enabled = false;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string value = raw.Trim();
        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
            return true;
        }

        return false;
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
    /// ImGui rendering through the Vulkan pipeline.
    /// </summary>
    public static bool EnableImGui
        => ResolveImGuiPreference(true);

    public static bool EnableDescriptorIndexing
        => ResolveDescriptorIndexingPreference(RuntimeEngine.EffectiveSettings.EnableVulkanDescriptorIndexing);

    public static EVulkanCapabilityTier RequestedCapabilityTier
        => TryGetCapabilityTierEnvOverride(out EVulkanCapabilityTier tier)
            ? tier
            : EVulkanCapabilityTier.Vulkan13Production;

    public static EVulkanDescriptorBackend RequestedDescriptorBackend
        => TryGetDescriptorBackendEnvOverride(out EVulkanDescriptorBackend backend)
            ? backend
            : EVulkanDescriptorBackend.DescriptorIndexing;

    public static EVulkanProgramBindingBackend RequestedProgramBindingBackend
        => TryGetProgramBindingBackendEnvOverride(out EVulkanProgramBindingBackend backend)
            ? backend
            : EVulkanProgramBindingBackend.PipelineObjects;

    public static EVulkanFoveationBackend RequestedFoveationBackend
        => TryGetFoveationBackendEnvOverride(out EVulkanFoveationBackend backend)
            ? backend
            : EVulkanFoveationBackend.Off;

    public static EVulkanRayTracingBackend RequestedRayTracingBackend
        => TryGetRayTracingBackendEnvOverride(out EVulkanRayTracingBackend backend)
            ? backend
            : EVulkanRayTracingBackend.Off;

    public static EVulkanBindlessMaterialMode ActiveBindlessMaterialMode
        => ResolveBindlessMaterialMode(
            RuntimeEngine.EffectiveSettings.VulkanBindlessMaterialMode,
            RuntimeEngine.EffectiveSettings.EnableVulkanBindlessMaterialTable);

    public static bool EnableBindlessMaterialTable
        => ActiveBindlessMaterialMode != EVulkanBindlessMaterialMode.Disabled &&
           ResolveBindlessMaterialTablePreference(true);

    public static bool RequireBindlessMaterialTable
        => ActiveBindlessMaterialMode == EVulkanBindlessMaterialMode.Required;

    public static bool DiagnoseBindlessMaterialTable
        => ActiveBindlessMaterialMode == EVulkanBindlessMaterialMode.Diagnostics ||
           ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics;

    public static bool EnableDescriptorContractValidation
        => ResolveDescriptorContractValidationPreference(RuntimeEngine.EffectiveSettings.ValidateVulkanDescriptorContracts);

    public static bool EnableRtxIoVulkanDecompression
        => ResolveRtxIoVulkanDecompressionPreference(true);

    public static bool EnableRtxIoVulkanCopyMemoryIndirect
        => ResolveRtxIoVulkanCopyMemoryIndirectPreference(true);

    public static EVulkanGeometryFetchMode ActiveGeometryFetchMode
        => ResolveGeometryFetchMode(RuntimeEngine.EffectiveSettings.VulkanGeometryFetchMode);

    public static EVulkanQueueOverlapMode ResolveQueueOverlapMode(EVulkanQueueOverlapMode requested)
    {
        if (requested != EVulkanQueueOverlapMode.Auto)
            return requested;

        if (!IsActive)
            return EVulkanQueueOverlapMode.GraphicsOnly;

        return ActiveProfile switch
        {
            EVulkanGpuDrivenProfile.ShippingFast => EVulkanQueueOverlapMode.GraphicsOnly,
            EVulkanGpuDrivenProfile.DevParity => EVulkanQueueOverlapMode.GraphicsCompute,
            EVulkanGpuDrivenProfile.Diagnostics => EVulkanQueueOverlapMode.GraphicsComputeTransfer,
            _ => EVulkanQueueOverlapMode.GraphicsOnly,
        };
    }

    /// <summary>
    /// Returns <c>true</c> when the active GPU-driven profile enforces strict no-fallback behavior
    /// across rendering backends (including OpenGL).
    /// </summary>
    public static bool EnforceStrictNoFallbacks
        => ActiveProfile == EVulkanGpuDrivenProfile.ShippingFast;
}

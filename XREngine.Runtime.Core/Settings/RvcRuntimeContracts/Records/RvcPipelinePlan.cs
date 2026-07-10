using System.Numerics;

namespace XREngine;

public readonly record struct RvcPipelinePlan(
    RvcRenderingSettings Settings,
    RvcQualitySettings Quality,
    RvcPipelineResolution Resolution,
    RvcCapabilityMatrix Capabilities,
    RvcCounterReadbackContract CounterReadback,
    RvcVisibilityPassPlan Visibility,
    RvcShadeletDensityPolicy ShadeletDensity,
    RvcShadeletDeduplicationPlan ShadeletDeduplication,
    RvcFoveatedShadingRatePlan FoveatedShadingRate,
    RvcWideInsetCompositionPolicy WideInsetComposition,
    RvcMaterialBindingContract MaterialBinding,
    RvcSharedLightingPlan SharedLighting,
    RvcReuseValidationPlan Reuse,
    RvcTemporalCachePolicy TemporalCache,
    RvcFoveatedResolveContract Resolve,
    RvcOpenXrVisibilityMaskPlan OpenXrVisibilityMask,
    RvcVisibilitySourcePathPlan VisibilitySourcePaths,
    RvcGpuPassExecutionPlan GpuPassExecution,
    RvcVulkanProductionPlan VulkanProduction,
    RvcExperimentalExtensionPlan ExperimentalExtensions)
{
    public bool UsesForwardPlusOracle => Resolution.EffectiveMode == ERvcPipelineMode.ForwardPlusOracle;

    public static RvcPipelinePlan Build(
        in RvcRenderingSettings settings,
        in RvcQualitySettings quality,
        in RvcPipelineResolution resolution,
        in RvcCapabilityMatrix capabilities)
        => new(
            settings,
            quality,
            resolution,
            capabilities,
            RvcCounterReadbackContract.Default,
            RvcVisibilityPassPlan.Default,
            new RvcShadeletDensityPolicy(
                ERvcShadeletDensity.Rate1x1,
                ERvcShadeletDensity.Rate1x1,
                ERvcShadeletDensity.Rate2x2,
                quality.PeripheralMaxRate,
                ForceNearFieldAndUiTo1x1: true,
                quality.ForceFullResNearDistanceMeters),
            RvcShadeletDeduplicationPlan.Default,
            RvcFoveatedShadingRatePlan.FromBackend(resolution.FoveationRateBackend),
            RvcWideInsetCompositionPolicy.Default,
            RvcMaterialBindingContract.FromResolution(resolution),
            RvcSharedLightingPlan.CreateDefault(Vector3.Zero),
            new RvcReuseValidationPlan(
                RvcReusePolicy.DefaultsWithStereoOff with
                {
                    MaxNormalAngleDegrees = quality.ReuseMaxNormalAngleDegrees,
                    MaxDepthDeltaMeters = quality.ReuseMaxDepthDeltaMeters,
                    MaxRoughnessBucketDelta = quality.ReuseMaxRoughnessBucketDelta,
                },
                RvcEdgeRejectionPolicy.Conservative,
                RequireAbHarnessBeforeStereoDefault: true,
                KeepSharpSpecularPerView: true),
            RvcTemporalCachePolicy.Default,
            RvcFoveatedResolveContract.Default with
            {
                FovealAntiAliasingPath = quality.FovealAntiAliasingPath,
            },
            RvcOpenXrVisibilityMaskPlan.Resolve(
                requested: settings.PipelineMode != ERvcPipelineMode.Off,
                extensionEnabled: capabilities.OpenXrVisibilityMaskSupported),
            RvcVisibilitySourcePathPlan.Resolve(
                resolution,
                staticMeshPathAvailable: capabilities.StaticMeshVisibilitySourceSupported || capabilities.VisibilityTargetsAvailable,
                skinnedComputePathAvailable: capabilities.SkinnedComputeVisibilitySourceSupported || capabilities.VisibilityTargetsAvailable,
                zeroReadbackMaterialRowsAvailable: capabilities.ZeroReadbackIndirectVisibilitySourceSupported || resolution.DescriptorBackend != ERvcDescriptorBackend.None,
                meshletPathAvailable: capabilities.MeshletVisibilitySourceSupported || capabilities.VulkanMeshShaderSupported),
            RvcGpuPassExecutionPlan.FromPlan(
                resolution,
                visibilityTargetsImplemented: capabilities.VisibilityTargetsAvailable,
                materialCacheImplemented: capabilities.VisibilityTargetsAvailable && resolution.DescriptorBackend != ERvcDescriptorBackend.None,
                sharedLightingImplemented: capabilities.VisibilityTargetsAvailable && (settings.PeripheralLightAggregationEnabled || resolution.IsRvcActive),
                temporalResolveImplemented: capabilities.VisibilityTargetsAvailable && (settings.TemporalReuseEnabled || resolution.IsRvcActive),
                diagnosticOverlayImplemented: settings.DiagnosticOverlayEnabled || resolution.IsRvcActive),
            RvcVulkanProductionPlan.Resolve(
                resolution,
                capabilities,
                dynamicRenderingAvailable: capabilities.VulkanDynamicRenderingSupported,
                synchronization2Available: capabilities.VulkanSynchronization2Supported,
                meshShaderAvailable: capabilities.VulkanMeshShaderSupported,
                timelineSemaphoreAvailable: capabilities.VulkanTimelineSemaphoreSupported),
            RvcExperimentalExtensionPlan.Resolve(
                capabilities,
                meshShaderAvailable: capabilities.VulkanMeshShaderSupported,
                allowPrototypeExtensions: false));

    public static RvcPipelinePlan Build(
        in RvcRenderingSettings settings,
        in RvcPipelineResolution resolution,
        in RvcCapabilityMatrix capabilities)
        => Build(settings, RvcQualitySettings.Defaults, resolution, capabilities);
}

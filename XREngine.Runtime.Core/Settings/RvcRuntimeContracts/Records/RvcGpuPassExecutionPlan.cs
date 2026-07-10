namespace XREngine;

public readonly record struct RvcGpuPassExecutionPlan(
    ERvcGpuPassStage PlannedStages,
    ERvcGpuPassStage BackendImplementedStages,
    ERvcGpuPassStage ForwardPlusFallbackStages,
    bool UsesFrameGraphResources,
    bool UsesDelayedCounterReadback,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public ERvcGpuPassStage MissingBackendStages => PlannedStages & ~BackendImplementedStages;
    public bool IsFullyImplemented => MissingBackendStages == ERvcGpuPassStage.None && FallbackReason == ERvcFallbackReason.None;

    public static RvcGpuPassExecutionPlan FromPlan(
        in RvcPipelineResolution resolution,
        bool visibilityTargetsImplemented,
        bool materialCacheImplemented,
        bool sharedLightingImplemented,
        bool temporalResolveImplemented,
        bool diagnosticOverlayImplemented)
    {
        ERvcGpuPassStage planned = ResolvePlannedStages(resolution.RequestedMode);
        ERvcGpuPassStage implemented = ERvcGpuPassStage.None;

        if (visibilityTargetsImplemented)
        {
            implemented |=
                ERvcGpuPassStage.VisibilityTargets |
                ERvcGpuPassStage.OpenXrVisibilityMaskStencil |
                ERvcGpuPassStage.AttributeReconstruction |
                ERvcGpuPassStage.HzbRejection;
        }

        if (materialCacheImplemented)
        {
            implemented |=
                ERvcGpuPassStage.PixelToShadeletMap |
                ERvcGpuPassStage.MaterialShadeletShading |
                ERvcGpuPassStage.FoveatedShadingRate |
                ERvcGpuPassStage.ReuseValidation;
        }

        if (sharedLightingImplemented)
        {
            implemented |=
                ERvcGpuPassStage.HeadSpaceLightClusters |
                ERvcGpuPassStage.SharedLighting;
        }

        if (temporalResolveImplemented)
        {
            implemented |=
                ERvcGpuPassStage.TemporalCache |
                ERvcGpuPassStage.FoveatedResolve |
                ERvcGpuPassStage.TransparencyForwardPlus;
        }

        if (diagnosticOverlayImplemented)
            implemented |= ERvcGpuPassStage.DiagnosticOverlay;

        ERvcGpuPassStage missing = planned & ~implemented;
        bool cacheRequested = resolution.RequestedMode is not ERvcPipelineMode.Off and not ERvcPipelineMode.ForwardPlusOracle;
        return new(
            planned,
            implemented,
            cacheRequested ? missing : ERvcGpuPassStage.None,
            UsesFrameGraphResources: cacheRequested,
            UsesDelayedCounterReadback: cacheRequested,
            cacheRequested && missing != ERvcGpuPassStage.None
                ? (resolution.FallbackReason == ERvcFallbackReason.None ? ERvcFallbackReason.MissingVisibilityTargets : resolution.FallbackReason)
                : ERvcFallbackReason.None,
            cacheRequested && missing != ERvcGpuPassStage.None
                ? "One or more RVC GPU pass stages still lack a backend implementation."
                : "RVC GPU pass execution plan is resolved.");
    }

    private static ERvcGpuPassStage ResolvePlannedStages(ERvcPipelineMode mode)
        => mode switch
        {
            ERvcPipelineMode.VisibilityOnlyDebug =>
                ERvcGpuPassStage.VisibilityTargets |
                ERvcGpuPassStage.OpenXrVisibilityMaskStencil |
                ERvcGpuPassStage.AttributeReconstruction |
                ERvcGpuPassStage.HzbRejection |
                ERvcGpuPassStage.DiagnosticOverlay,
            ERvcPipelineMode.MaterialCache =>
                ResolvePlannedStages(ERvcPipelineMode.VisibilityOnlyDebug) |
                ERvcGpuPassStage.PixelToShadeletMap |
                ERvcGpuPassStage.MaterialShadeletShading |
                ERvcGpuPassStage.FoveatedShadingRate |
                ERvcGpuPassStage.ReuseValidation,
            ERvcPipelineMode.SharedLighting =>
                ResolvePlannedStages(ERvcPipelineMode.MaterialCache) |
                ERvcGpuPassStage.HeadSpaceLightClusters |
                ERvcGpuPassStage.SharedLighting,
            ERvcPipelineMode.Full =>
                ResolvePlannedStages(ERvcPipelineMode.SharedLighting) |
                ERvcGpuPassStage.TemporalCache |
                ERvcGpuPassStage.FoveatedResolve |
                ERvcGpuPassStage.TransparencyForwardPlus,
            _ => ERvcGpuPassStage.None,
        };
}

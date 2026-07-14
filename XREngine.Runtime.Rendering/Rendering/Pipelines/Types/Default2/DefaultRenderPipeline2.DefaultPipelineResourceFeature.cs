namespace XREngine.Rendering;

public partial class DefaultRenderPipeline2
{
    [Flags]
    private enum DefaultPipelineResourceFeature : ulong
    {
        None = 0,
        DeferredMsaaEnabled = 1UL << 0,
        ForwardDepthPrePassEnabled = 1UL << 1,
        ForwardPrePassSharesGBufferTargets = 1UL << 2,
        ForwardDepthPrePassHalfResolution = 1UL << 3,
        ForwardDepthPrePassQuarterResolution = 1UL << 4,
        VendorUpscalePreferred = 1UL << 5,
        GtaoFullResolution = 1UL << 6,
        GtaoQuarterResolution = 1UL << 7,
        WeightedBlendedOitEnabled = 1UL << 8,
        ExactTransparencyEnabled = 1UL << 9,
        MotionBlurEnabled = 1UL << 10,
        DepthOfFieldEnabled = 1UL << 11,
        OpenXrVulkanDesktopSafePath = 1UL << 12,
        AmbientOcclusionResourcesEnabled = 1UL << 13,
        BloomResourcesEnabled = 1UL << 14,
        TemporalResourcesEnabled = 1UL << 15,
        MinimalDirectCapture = 1UL << 16,
        MsaaTargetsEnabled = 1UL << 17,
        AtmosphereResourcesEnabled = 1UL << 18,
        VolumetricFogResourcesEnabled = 1UL << 19,
        RestirGiResourcesEnabled = 1UL << 20,
        LightVolumeGiResourcesEnabled = 1UL << 21,
        RadianceCascadeGiResourcesEnabled = 1UL << 22,
        SurfelGiResourcesEnabled = 1UL << 23,
        VoxelConeTracingResourcesEnabled = 1UL << 24,
        DebugVisualizationResourcesEnabled = 1UL << 25,
        // AO mode field [bits 26-29]: 0=disabled/safe-path, (int)NormalizedType+1 for active modes.
        AoModeFieldBit0 = 1UL << 26,
        AoModeFieldBit1 = 1UL << 27,
        AoModeFieldBit2 = 1UL << 28,
        AoModeFieldBit3 = 1UL << 29,
    }
}

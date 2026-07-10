namespace XREngine;

public readonly record struct RvcRenderingSettings(
    ERvcPipelineMode PipelineMode,
    bool QuadViewEnabled,
    bool StereoReuseEnabled,
    bool InsetWideReuseEnabled,
    bool TemporalReuseEnabled,
    bool PeripheralLightAggregationEnabled,
    bool DiagnosticOverlayEnabled,
    ERvcDebugViewMode DebugViewMode,
    ERvcLightGridSpace LightGridSpace)
{
    public static RvcRenderingSettings Defaults => new(
        ERvcPipelineMode.Off,
        QuadViewEnabled: false,
        StereoReuseEnabled: false,
        InsetWideReuseEnabled: true,
        TemporalReuseEnabled: false,
        PeripheralLightAggregationEnabled: false,
        DiagnosticOverlayEnabled: false,
        ERvcDebugViewMode.Disabled,
        ERvcLightGridSpace.WorldAlignedCameraRelative);
}

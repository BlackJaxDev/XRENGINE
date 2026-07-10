namespace XREngine;

public readonly record struct RvcPipelineResolution(
    ERvcPipelineMode RequestedMode,
    ERvcPipelineMode EffectiveMode,
    bool IsRvcActive,
    ERvcDescriptorBackend DescriptorBackend,
    ERvcFoveationRateBackend FoveationRateBackend,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public bool UsesForwardPlusFallback => !IsRvcActive && FallbackReason != ERvcFallbackReason.None;
}

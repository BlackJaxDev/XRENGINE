namespace XREngine.Rendering.Profiling;

public enum RenderProfileCpuSamplingPolicy
{
    Disabled,
    AggregateOnly,
    TargetedSpans,
    ExternalSamplerOptional,
    ExternalSamplerRequired,
}

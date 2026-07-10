namespace XREngine;

public enum ERenderOutputCompletionRequirement
{
    None,
    BeforeConsumer,
    BeforePresent,
    GpuCompleteBeforeRuntimeRelease,
}

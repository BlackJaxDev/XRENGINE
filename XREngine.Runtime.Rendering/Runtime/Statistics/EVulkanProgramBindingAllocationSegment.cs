namespace XREngine;

/// <summary>
/// Raw allocation checkpoints within the warmed persistent program-binding
/// artifact path. These counters deliberately avoid nested profiler scopes so
/// the measurement cannot attribute profiler bookkeeping to the renderer work.
/// </summary>
public enum EVulkanProgramBindingAllocationSegment
{
    Setup,
    PublisherScope,
    EligibilityGap,
    EligibilityScope,
    ArtifactKeyAndGeneration,
    LookupScope,
    ReusePublication,
    Count,
}

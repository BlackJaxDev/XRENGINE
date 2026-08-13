namespace XREngine.Rendering.Profiling;

public enum RenderProfileMutationPolicy
{
    StableReuse,
    ForcedDirtyEveryFrame,
    DirtyEveryNFrames,
    ResourceChurn,
    DescriptorChurn,
    PipelineChurn,
}

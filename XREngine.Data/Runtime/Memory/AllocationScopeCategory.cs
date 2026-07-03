namespace XREngine.Data.Runtime.Memory;

public enum AllocationScopeCategory
{
    RuntimeSystem,
    EcsSystem,
    RenderPass,
    RenderSubmission,
    NetworkCodec,
    VrInput,
    AnimationIk,
    GpuUploadPreparation,
    EditorUi,
    Diagnostics,
}

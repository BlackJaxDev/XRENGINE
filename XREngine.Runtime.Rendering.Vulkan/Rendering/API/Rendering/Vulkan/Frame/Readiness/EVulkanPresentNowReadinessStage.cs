namespace XREngine.Rendering.Vulkan;

/// <summary>Foreground preparation stages that may block before output acquisition.</summary>
internal enum EVulkanPresentNowReadinessStage : byte
{
    MeshMaterialization,
    RequiredUploadPreparation,
    RequiredUploadCompletion,
    FramePlanSeal,
    PipelineCompilation,
    QueueSubmission,
    Presentation,
}

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Compact primary-recorder dispatch identity compiled from a frame operation.
/// </summary>
internal enum EVulkanPrimaryPlanNodeKind : byte
{
    Unsupported = 0,
    TextureUpload,
    Blit,
    Clear,
    TransformFeedback,
    Query,
    MeshDraw,
    IndirectDraw,
    MeshTaskDispatchIndirectCount,
    ComputeDispatch,
    ComputeDispatchIndirect,
    BufferCopy,
    SubmissionMarker,
    MemoryBarrier,
    PublishFramebufferForSampling,
    DlssUpscale,
    DlssFrameGeneration,
    QueueOwnershipTransfer,
    EndRendering,
    PreparePresent,
    ReleaseExternalImageOwnership,
}

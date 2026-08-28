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
    /// <summary>
    /// First sealed GPU-only advanced visibility producer/raster lane. Its
    /// concrete frame-slot storage is prepared after plan sealing and before
    /// native recording; no CPU visibility count is observed by this opcode.
    /// </summary>
    AdvancedVisibility,
    QueueOwnershipTransfer,
    EndRendering,
    PreparePresent,
    ReleaseExternalImageOwnership,
}

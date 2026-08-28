namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Physical usage lanes owned by the canonical Vulkan frame-data arena.
/// Callers select a lane from the native operations the allocation must support;
/// payload names and content semantics do not influence allocation behavior.
/// </summary>
internal enum EVulkanFrameDataLane : byte
{
    TransferUpload,
    TransferStaging,
    Readback,
    Uniform,
    Storage,
    /// <summary>
    /// Boundary-reserved storage for canonical advanced-scene publication
    /// images. This lane never grows while a frame is being prepared.
    /// </summary>
    AdvancedSceneStorage,
    Indirect,
    Count,
}

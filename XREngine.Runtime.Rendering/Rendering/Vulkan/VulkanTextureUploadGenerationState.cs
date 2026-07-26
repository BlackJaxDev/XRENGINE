namespace XREngine.Rendering.Vulkan;

internal enum VulkanTextureUploadGenerationState
{
    Decoded,
    PrepQueued,
    PrepDeferred,
    PrepRunning,
    PrepReady,
    UploadQueued,
    UploadRecording,
    GpuUploadPending,
    TransferSubmitted,
    TransferComplete,
    Uploaded,
    DescriptorPublishPending,
    Published,
    Retired,
    Canceled,
    Failed,
}

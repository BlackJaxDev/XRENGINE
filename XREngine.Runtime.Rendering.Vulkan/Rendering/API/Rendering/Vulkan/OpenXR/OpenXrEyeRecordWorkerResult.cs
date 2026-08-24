namespace XREngine.Rendering.Vulkan;

internal readonly record struct OpenXrEyeRecordWorkerResult(
    bool Success,
    OpenXrRecordedEyeCommandBuffer Recorded,
    int ThreadId,
    TimeSpan RecordTime,
    string? ErrorMessage,
    long StartTimestamp = 0,
    long EndTimestamp = 0,
    VulkanImportedTexturePendingUpload[]? RecordedUploads = null);

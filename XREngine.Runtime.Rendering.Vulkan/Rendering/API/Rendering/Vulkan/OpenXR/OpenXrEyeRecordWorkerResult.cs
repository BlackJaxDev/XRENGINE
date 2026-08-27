namespace XREngine.Rendering.Vulkan;

using System.Runtime.ExceptionServices;

internal readonly record struct OpenXrEyeRecordWorkerResult(
    bool Success,
    OpenXrRecordedEyeCommandBuffer Recorded,
    int ThreadId,
    TimeSpan RecordTime,
    string? ErrorMessage,
    long StartTimestamp = 0,
    long EndTimestamp = 0,
    VulkanImportedTexturePendingUpload[]? RecordedUploads = null,
    ExceptionDispatchInfo? Failure = null);

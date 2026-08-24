namespace XREngine.Rendering.Vulkan;

internal readonly record struct OpenXrEyeRecordWorkerBatchResult(
    OpenXrEyeRecordWorkerResult Left,
    OpenXrEyeRecordWorkerResult Right,
    TimeSpan WaitForWorkersTime);

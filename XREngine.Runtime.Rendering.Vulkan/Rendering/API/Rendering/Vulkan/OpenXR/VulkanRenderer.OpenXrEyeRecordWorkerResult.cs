namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct OpenXrEyeRecordWorkerResult(
        bool Success,
        OpenXrRecordedEyeCommandBuffer Recorded,
        int ThreadId,
        TimeSpan RecordTime,
        string? ErrorMessage);
}

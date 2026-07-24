namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct OpenXrEyeRecordWorkerBatchResult(
        OpenXrEyeRecordWorkerResult Left,
        OpenXrEyeRecordWorkerResult Right,
        TimeSpan WaitForWorkersTime);
}

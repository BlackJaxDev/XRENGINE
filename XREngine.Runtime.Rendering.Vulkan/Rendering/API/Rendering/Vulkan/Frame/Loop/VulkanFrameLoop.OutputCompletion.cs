namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    internal bool TryReserveOutputCompletion(
        in RenderOutputRequest output,
        XRFrameBuffer targetFrameBuffer,
        out RenderOutputCompletionBackendReservation reservation)
    {
        VulkanTimelineGpuFence fence = _commandRuntime.RentTimelineGpuFence();
        if (_frameOperationQueue.TryReserveOutputCompletion(
                in output,
                targetFrameBuffer,
                fence,
                out reservation))
            return true;

        fence.Fail();
        reservation = default;
        return false;
    }

    internal void CompleteOutputCompletionAuthoring(
        in RenderOutputCompletionBackendReservation reservation,
        bool succeeded,
        out XRGpuFence? completionFence)
    {
        completionFence = reservation.Fence;
        _frameOperationQueue.CompleteOutputCompletionAuthoring(
            in reservation,
            succeeded);
    }
}

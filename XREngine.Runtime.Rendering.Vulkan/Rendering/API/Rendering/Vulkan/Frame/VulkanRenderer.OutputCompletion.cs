namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    internal override bool TryReserveOutputCompletion(
        in RenderOutputRequest output,
        XRFrameBuffer targetFrameBuffer,
        out RenderOutputCompletionBackendReservation reservation)
        => _frameLoop.TryReserveOutputCompletion(
            in output,
            targetFrameBuffer,
            out reservation);

    internal override void CompleteOutputCompletionAuthoring(
        in RenderOutputCompletionBackendReservation reservation,
        bool succeeded,
        out XRGpuFence? completionFence)
        => _frameLoop.CompleteOutputCompletionAuthoring(
            in reservation,
            succeeded,
            out completionFence);
}

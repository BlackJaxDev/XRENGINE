namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    internal VulkanTimelineGpuFence RentUiPublicationFence()
    {
        VulkanTimelineGpuFence fence = _commandRuntime.RentTimelineGpuFence();
        fence.SetAuthoringContext(RuntimeEngine.Rendering.State.RenderFrameId, -1);
        return fence;
    }

    internal void DiscardPendingUiPublication(ulong receiptId)
    {
        _ = MeshOperationRequests.DiscardPendingUiPublicationRequests(receiptId);
        _ = _frameOperationQueue.DiscardPendingUiPublicationOperations(receiptId);
    }
}

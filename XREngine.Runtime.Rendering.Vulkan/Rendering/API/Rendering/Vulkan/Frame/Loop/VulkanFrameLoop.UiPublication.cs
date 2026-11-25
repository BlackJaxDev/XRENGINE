namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    internal VulkanTimelineGpuFence RentUiPublicationFence()
    {
        VulkanTimelineGpuFence fence = _commandRuntime.RentTimelineGpuFence();
        fence.SetAuthoringContext(RuntimeEngine.Rendering.State.RenderFrameId, -1);
        return fence;
    }

    /// <summary>
    /// Reports whether queued requests, pending operations, or prepared readiness draws still
    /// hold draws of one UI publication receipt, so that a later plan can still bind it.
    /// </summary>
    internal bool HasPendingUiPublicationWork(ulong receiptId)
    {
        if (MeshOperationRequests.HasPendingUiPublicationRequests(receiptId) ||
            _frameOperationQueue.HasPendingUiPublicationOperations(receiptId))
            return true;
        for (int index = 0; index < _preparedMeshIngress.Count; index++)
            if (_preparedMeshIngress.GetEntry(index).Context.UiPublicationReceiptId == receiptId)
                return true;
        return false;
    }

    internal void DiscardPendingUiPublication(ulong receiptId)
    {
        _ = MeshOperationRequests.DiscardPendingUiPublicationRequests(receiptId);
        _ = _frameOperationQueue.DiscardPendingUiPublicationOperations(receiptId);
    }
}

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

    /// <summary>
    /// Writes where the pending draws of one UI receipt wait, for a stall diagnostic: "q" in the mesh request
    /// queue, "o" in the frame operation queue, "i" in the prepared mesh ingress, or "-" for nowhere.
    /// </summary>
    internal string DescribePendingUiPublicationWork(ulong receiptId)
    {
        bool queued = MeshOperationRequests.HasPendingUiPublicationRequests(receiptId);
        bool operations = _frameOperationQueue.HasPendingUiPublicationOperations(receiptId);
        bool ingress = false;
        for (int index = 0; index < _preparedMeshIngress.Count && !ingress; index++)
            ingress = _preparedMeshIngress.GetEntry(index).Context.UiPublicationReceiptId == receiptId;
        string where = (queued ? "q" : string.Empty) + (operations ? "o" : string.Empty) + (ingress ? "i" : string.Empty);
        return where.Length == 0 ? "-" : where;
    }

    internal void DiscardPendingUiPublication(ulong receiptId)
    {
        _ = MeshOperationRequests.DiscardPendingUiPublicationRequests(receiptId);
        _ = _frameOperationQueue.DiscardPendingUiPublicationOperations(receiptId);
    }
}

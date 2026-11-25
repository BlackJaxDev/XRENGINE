using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    protected override bool TryReserveUiPublicationReceipt(
        ulong receiptId, UIPublication publication, int expectedDrawIntents,
        bool requiresSwapchainPresentation)
        => VulkanUiPublicationReceiptLedger.TryReserve(
            this, receiptId, publication, expectedDrawIntents,
            requiresSwapchainPresentation);

    protected override void OnUiPublicationAuthoringFailed(ulong receiptId, string reason)
        => VulkanUiPublicationReceiptLedger.Fail(receiptId);

    protected override void CancelUiPublicationReceiptBeforeAuthoringCore(ulong receiptId)
    {
        _frameLoop.DiscardPendingUiPublication(receiptId);
        VulkanUiPublicationReceiptLedger.RetireFailedAuthoring(receiptId);
    }

    protected override bool CompleteUiPublicationReceiptCore(
        ulong receiptId, bool succeeded, string? failureReason, out XRGpuFence? completionFence)
    {
        // The fence is registered with the exact primary artifact only after
        // every tagged draw records. Per-run markers cannot certify the canvas.
        completionFence = succeeded ? _frameLoop.RentUiPublicationFence() : null;
        if (VulkanUiPublicationReceiptLedger.Seal(receiptId, succeeded, completionFence))
            return true;
        _frameLoop.DiscardPendingUiPublication(receiptId);
        VulkanUiPublicationReceiptLedger.RetireFailedAuthoring(receiptId);
        if (completionFence is VulkanTimelineGpuFence failedFence)
            failedFence.Fail(EGpuFenceFailureSite.OutputCohortIncomplete,
                EGpuFenceNativeSubmission.NotCalled);
        completionFence = null;
        return false;
    }

    public override bool IsUiPublicationReceiptRetired(ulong receiptId)
        => VulkanUiPublicationReceiptLedger.IsRetired(receiptId);

    internal override bool TryAcceptPresentedUiPublicationReceipt(
        ulong receiptId, uint imageIndex, ulong outputGeneration,
        ulong sourceFrameId)
        => VulkanUiPublicationReceiptLedger.TryPresent(
            receiptId, imageIndex, outputGeneration, sourceFrameId);

    internal override bool TryAcceptReplayedUiPublicationReceipt(
        ulong receiptId, uint sourceImageIndex, ulong sourceOutputGeneration,
        uint targetImageIndex, ulong targetOutputGeneration, ulong sourceFrameId)
        => VulkanUiPublicationReceiptLedger.TryAcceptReplay(this, receiptId,
            sourceImageIndex, sourceOutputGeneration, targetImageIndex,
            targetOutputGeneration, sourceFrameId);

    internal override void InvalidatePresentedUiPublicationImage(
        uint imageIndex, ulong outputGeneration)
        => VulkanUiPublicationReceiptLedger.InvalidateImage(this, imageIndex, outputGeneration);
}

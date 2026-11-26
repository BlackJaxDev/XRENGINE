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

    /// <summary>Reports whether queued or prepared draws of one UI receipt can still reach a plan.</summary>
    internal bool HasPendingUiPublicationWork(ulong receiptId)
        => _frameLoop.HasPendingUiPublicationWork(receiptId);

    /// <summary>
    /// Each swapchain image has a primary command buffer that keeps its last UI packet, because a frame can
    /// submit that buffer again without recording it. Up to <see cref="DesktopFramesInFlight"/> more packets are
    /// authored or queued for the next frames.
    /// </summary>
    public override int UiPublicationRetentionDepth
        => Math.Max(1, _outputRuntime.Desktop.Images?.Length ?? 0) + DesktopFramesInFlight;

    /// <summary>
    /// A canvas seals a UI packet every render frame, but only an accepted frame plan releases the packets that
    /// no plan took. While frames are rejected, for example while new programs compile after the UI changes,
    /// such packets fill every cohort of the canvas and the UI stops drawing. This releases them without a plan.
    /// It keeps the packets of the last two frames, because the frame loop can still be planning them, and the
    /// required world UI attempt, which another path owns.
    /// </summary>
    public override int RetireAbandonedUiPublicationReceipts()
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (frameId < 2)
            return 0;
        ulong keptWorldReceiptId = 0UL;
        if (TryGetDesktopWindowHost(out var desktopHost) && desktopHost is XRWindow desktopWindow)
            _ = desktopWindow.TryGetRequiredVulkanWorldUiAttempt(
                out _, out _, out _, out keptWorldReceiptId, out _);
        return VulkanUiPublicationReceiptLedger.RetireUnclaimedBefore(this, frameId - 1, keptWorldReceiptId);
    }

    public override string DescribeUiPublicationReceipt(ulong receiptId)
    {
        // The queues take their own locks, so the pending work is read outside the ledger gate.
        Span<ulong> live = stackalloc ulong[64];
        int count = VulkanUiPublicationReceiptLedger.CopyLiveIds(this, live);
        System.Text.StringBuilder pending = new(" pending:");
        for (int index = 0; index < count; index++)
            pending.Append($" {live[index]}={_frameLoop.DescribePendingUiPublicationWork(live[index])}");
        return VulkanUiPublicationReceiptLedger.Describe(receiptId) + pending;
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

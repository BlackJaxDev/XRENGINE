namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    /// <inheritdoc />
    internal override bool TryReserveFrameViewHistoryCandidate(
        in RenderFrameViewHistoryCandidateToken candidate,
        in RenderOutputRequest output,
        XRFrameBuffer? targetFrameBuffer,
        out RenderFrameViewHistoryBackendReservation reservation)
        => _framePlanner.Operations.TryReserveFrameViewHistory(
            in candidate,
            in output,
            targetFrameBuffer,
            out reservation);

    /// <inheritdoc />
    internal override void CompleteFrameViewHistoryCandidateAuthoring(
        in RenderFrameViewHistoryBackendReservation reservation,
        bool succeeded)
        => _framePlanner.Operations.CompleteFrameViewHistoryAuthoring(
            in reservation,
            succeeded);
}

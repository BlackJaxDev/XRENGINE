namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Reads the latest production-frame telemetry only when it belongs to the
    /// supplied accepted receipt. This does not wait for GPU completion.
    /// </summary>
    internal bool TryGetExplicitProductionFrameTelemetry(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        out VulkanFrameTelemetryPublication publication)
    {
        publication = default;
        if (!IsAuthenticExplicitProductionReceipt(in receipt) ||
            !_frameTelemetry.TryGetLatestPublication(out publication))
        {
            return false;
        }

        VulkanFrameRootIdentity identity = publication.Identity;
        return publication.Outcome == EVulkanFrameOutcome.Completed &&
               identity.EngineFrameNumber == receipt.ExplicitFrameNumber &&
               identity.RenderFrameNumber == receipt.EngineFrameId &&
               identity.FrameSlot == checked((int)receipt.ExpectedFrameSlot) &&
               identity.Output.OutputGeneration == receipt.TargetGeneration;
    }
}

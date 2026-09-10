namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal bool TryGetExplicitProductionFrameTelemetry(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        out VulkanFrameTelemetryPublication publication)
        => _frameLoop.TryGetExplicitProductionFrameTelemetry(in receipt, out publication);
}

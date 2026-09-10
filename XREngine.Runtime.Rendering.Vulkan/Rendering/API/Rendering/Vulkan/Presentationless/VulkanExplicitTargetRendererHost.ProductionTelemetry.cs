namespace XREngine.Rendering.Vulkan;

public sealed unsafe partial class VulkanExplicitTargetRendererHost
{
    /// <summary>
    /// Reads the lifecycle telemetry for this exact accepted production receipt.
    /// The method is nonblocking and returns false when a newer root has replaced it.
    /// </summary>
    public bool TryGetProductionFrameTelemetry(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        out VulkanFrameTelemetryPublication publication)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderer.TryGetExplicitProductionFrameTelemetry(in receipt, out publication);
    }
}

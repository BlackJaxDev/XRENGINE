namespace XREngine.Rendering.Vulkan;

public sealed unsafe partial class VulkanExplicitTargetRendererHost
{
    /// <summary>
    /// Renders a complete, current background capture into an engine-owned
    /// presentationless target. Exact native command artifacts may be replayed
    /// after their resources, bindings, and target have been validated. This
    /// never substitutes a stale image or defers required draw work.
    /// </summary>
    /// <remarks>
    /// Use <see cref="SubmitProductionFrame(Action{RenderFrameOutputDescription})"/>
    /// for outputs that require fresh command recording. Background submission
    /// is unavailable for WSI, component, or XR targets. The returned receipt
    /// has the same completion and readback ownership as a foreground receipt.
    /// </remarks>
    public VulkanExplicitProductionSubmissionReceipt SubmitBackgroundProductionFrame(
        Action<RenderFrameOutputDescription> buildFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buildFrame);
        return _renderer.SubmitExplicitProductionFrame(buildFrame, backgroundCapture: true);
    }
}

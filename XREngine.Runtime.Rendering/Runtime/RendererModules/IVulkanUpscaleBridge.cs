using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering;

/// <summary>
/// Stable rendering-kernel view of the temporary OpenGL-to-Vulkan upscale bridge.
/// Vulkan synchronization and native resources remain owned by the Vulkan leaf.
/// </summary>
internal interface IVulkanUpscaleBridge : IDisposable
{
    EVulkanUpscaleBridgeState State { get; }
    VulkanUpscaleBridgeFrameResources CurrentFrameResources { get; }
    string? LastStateReason { get; }
    string? PendingRecreateReason { get; }
    uint ResourceGeneration { get; }

    EVulkanUpscaleBridgeState PrepareForFrame(
        XRRenderPipelineInstance pipeline,
        VulkanUpscaleBridgeCapabilitySnapshot snapshot);

    bool TryResolveCurrentFrame(out VulkanUpscaleBridgeFrameInfo frame);
    bool TryExecuteVendorUpscale(
        IOpenGlVendorUpscaleBackendCapability renderer,
        XRFrameBuffer sourceColorFrameBuffer,
        XRFrameBuffer sourceDepthFrameBuffer,
        XRFrameBuffer sourceMotionFrameBuffer,
        XRFrameBuffer? sourceExposureFrameBuffer,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        out XRTexture? outputTexture,
        out TimeSpan dispatchDuration,
        out string failureReason);

    void NotifyVendorSelectionChanged(string reason);
    void NotifyCapabilitySnapshotChanged(string reason);
    void MarkNeedsRecreate(string reason);
    void Destroy(string reason);
}

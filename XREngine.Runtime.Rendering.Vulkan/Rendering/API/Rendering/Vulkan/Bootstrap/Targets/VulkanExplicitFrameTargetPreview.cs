namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Non-acquiring description of the next explicit output. It never owns a WSI
/// image or synchronization primitive; acquisition validates this token before
/// native recording begins.
/// </summary>
internal readonly record struct VulkanExplicitFrameTargetPreview(
    RenderFrameOutputDescription Output,
    ulong TargetGeneration,
    uint ExpectedFrameSlotIndex,
    SwapchainRecordingTarget CompatibilityTarget)
{
    internal bool IsCompatible(in VulkanFrameTargetLease lease)
        => Output.IsValid &&
           TargetGeneration != 0UL &&
           TargetGeneration == lease.Target.TargetGeneration &&
           ExpectedFrameSlotIndex == lease.Target.FrameSlotIndex &&
           CompatibilityTarget.ImageFormat == lease.ColorFormat &&
           CompatibilityTarget.DepthFormat == lease.DepthFormat &&
           Output.Properties.Width == lease.Target.Extent.Width &&
           Output.Properties.Height == lease.Target.Extent.Height &&
           Output.Properties.Layers == lease.Target.Layers &&
           Output.Properties.SampleCount == (uint)lease.Samples;
}

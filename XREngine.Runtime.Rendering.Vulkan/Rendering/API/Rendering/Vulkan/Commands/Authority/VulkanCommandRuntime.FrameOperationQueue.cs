namespace XREngine.Rendering.Vulkan;

/// <summary>Command-owned queue and capture operations over an explicit queue instance.</summary>
internal sealed partial class VulkanCommandRuntime
{
    internal void EnqueueFrameOperation(VulkanFrameOperationQueue queue, FrameOp operation, int passIndex)
        => queue.EnqueuePrepared(VulkanFrameOperationSemantics.Prepare(operation, passIndex));

    internal FrameOp[] DrainFrameOperations(VulkanFrameOperationQueue queue, bool excludeTextureUploads)
        => excludeTextureUploads ? queue.DrainExcludingTextureUploads() : queue.DrainPending();

    internal FrameOp[] DrainPrimaryFrameOperations(VulkanFrameOperationQueue queue, out FrameOp[] uploads)
        => queue.DrainForPrimary(out uploads);

    internal FrameOp[] CaptureFrameOperations(
        VulkanFrameOperationQueue queue, Action emitter, bool excludeTextureUploads)
        => queue.Capture(emitter, excludeTextureUploads);

    internal bool TryGetLastFrameOperation(VulkanFrameOperationQueue queue, XRFrameBuffer target, out FrameOp operation)
        => queue.TryGetLastForTarget(target, out operation);
}

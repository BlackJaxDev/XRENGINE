namespace XREngine.Rendering.Vulkan;

/// <summary>Command-owned queue and capture operations over an explicit queue instance.</summary>
internal sealed partial class VulkanCommandRuntime
{
    private const int ComplexFrameOperationResourceUseThreshold = 64;
    private int _frameOperationResourceUseHighWater;

    internal void EnqueueFrameOperation(VulkanFrameOperationQueue queue, FrameOp operation, int passIndex)
    {
        FrameOp prepared = VulkanFrameOperationSemantics.Prepare(operation, passIndex);
        ObserveFrameOperationResourceUseHighWater(prepared);
        queue.EnqueuePrepared(prepared);
    }

    /// <summary>
    /// Validates and publishes a producer-authored frame operation without allowing one
    /// malformed content draw to abort command recording for every other scene object.
    /// </summary>
    internal bool TryEnqueueContentFrameOperation(
        VulkanFrameOperationQueue queue,
        FrameOp operation,
        int passIndex,
        out string? failure)
    {
        try
        {
            FrameOp prepared = VulkanFrameOperationSemantics.Prepare(operation, passIndex);
            ObserveFrameOperationResourceUseHighWater(prepared);
            queue.EnqueuePrepared(prepared);
            failure = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            failure = ex.Message;
            Debug.VulkanWarningEvery(
                $"Vulkan.FrameOperation.Quarantined.{operation.GetType().Name}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Quarantined an invalid content frame operation before publication: {0}{1}{2}",
                ex.Message,
                BuildFrameOpFailureContext(operation),
                BuildFrameOpBindingCounts(operation));
            return false;
        }
    }

    private void ObserveFrameOperationResourceUseHighWater(FrameOp operation)
    {
        int resourceUseCount = operation.ResourceUsesReference.Count;
        int observed = Volatile.Read(ref _frameOperationResourceUseHighWater);
        while (resourceUseCount > observed)
        {
            int prior = Interlocked.CompareExchange(
                ref _frameOperationResourceUseHighWater,
                resourceUseCount,
                observed);
            if (prior == observed)
            {
                if (resourceUseCount > ComplexFrameOperationResourceUseThreshold)
                {
                    Debug.Vulkan(
                        "[Vulkan] Frame-operation resource-use high-water increased to {0}.{1}{2}",
                        resourceUseCount,
                        BuildFrameOpFailureContext(operation),
                        BuildFrameOpBindingCounts(operation));
                }

                return;
            }

            observed = prior;
        }
    }

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

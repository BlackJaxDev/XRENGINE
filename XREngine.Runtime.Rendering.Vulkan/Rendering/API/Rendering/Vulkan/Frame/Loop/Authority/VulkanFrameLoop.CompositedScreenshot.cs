using Silk.NET.Vulkan;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Vulkan;

/// <summary>Observational window captures made before acquired-image ownership passes to WSI.</summary>
internal sealed partial class VulkanFrameLoop
{
    private Queue<(BoundingRectangle Region, Action<ScreenshotReadbackResult> Callback)>? _compositedScreenshots;
    private Fence _pendingCompositedScreenshotFence;

    internal bool TryQueueCompositedScreenshotReadback(
        BoundingRectangle region,
        Action<ScreenshotReadbackResult> callback,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(callback);
        failure = null;
        if (!RuntimeEngine.IsRenderThread || _deviceLost || !_deviceContext.IsOperational)
            return RejectScreenshotReadback("Composited capture requires the operational Vulkan render thread.", out failure);
        if ((_compositedScreenshots?.Count ?? 0) >= ScreenshotReadbackRingSize)
            return RejectScreenshotReadback("The bounded composited screenshot queue is full.", out failure);

        // Allocated only on explicit capture requests, never during ordinary rendering.
        (_compositedScreenshots ??= new(ScreenshotReadbackRingSize)).Enqueue((region, callback));
        return true;
    }

    private void CaptureCompositedScreenshotsBeforePresent(in VulkanFrameAttempt attempt)
    {
        // Recovery may retry presentation after a host-side wait error. The copy
        // still owns the acquired image even though its request was dequeued.
        CompletePendingCompositedScreenshotCopy();
        if (_compositedScreenshots is not { Count: > 0 })
            return;
        if (!attempt.Submitted || attempt.GraphicsSignalValue == 0 ||
            OutputRuntime.Desktop.Images is not { } images || attempt.ImageIndex >= images.Length)
        {
            FailPendingCompositedScreenshots("No accepted desktop submission is available for window capture.");
            return;
        }

        // The final timeline signal joins scene and UI submissions, including secondary queues.
        // Do not consume the binary semaphore reserved for the subsequent WSI present.
        WaitForTimelineValue(_commandRuntime.Synchronization._graphicsTimelineSemaphore, attempt.GraphicsSignalValue);
        BlitImageInfo source = new(
            images[attempt.ImageIndex],
            OutputRuntime.Desktop.ImageFormat,
            ImageAspectFlags.ColorBit, 0, 1, 0,
            OutputRuntime.Desktop.Extent,
            ImageLayout.PresentSrcKhr,
            PipelineStageFlags.AllCommandsBit,
            AccessFlags.MemoryWriteBit | AccessFlags.MemoryReadBit,
            usage: ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);

        while (_compositedScreenshots.TryDequeue(out var request))
        {
            if (!TryQueueScreenshotReadbackCore(request.Region, false, request.Callback, out string? failure, source))
                CompleteCompositedScreenshotFailure(request.Callback, failure ?? "Composited capture failed.");
            if (_deviceLost)
                throw new InvalidOperationException("The Vulkan device was lost during composited screenshot readback.");
        }
    }

    private void CompletePendingCompositedScreenshotCopy()
    {
        if (_pendingCompositedScreenshotFence.Handle == 0)
            return;
        WaitForCompositedScreenshotFence(_pendingCompositedScreenshotFence);
        _pendingCompositedScreenshotFence = default;
    }

    private unsafe void WaitForCompositedScreenshotFence(Fence fence)
    {
        // This opt-in diagnostic cannot release image ownership while the copy is live.
        // The driver returns ErrorDeviceLost if work cannot complete; no queue/device idle.
        Result result = Api!.WaitForFences(_deviceContext.Device, 1, in fence, true, ulong.MaxValue);
        if (result == Result.Success)
            return;
        if (result == Result.ErrorDeviceLost)
            MarkDeviceLost("Composited screenshot fence reported device loss", "vkWaitForFences.CompositedScreenshot", result);
        throw new InvalidOperationException($"Composited screenshot completion failed: {result}.");
    }

    private void FailPendingCompositedScreenshots(string reason)
    {
        if (_compositedScreenshots is null)
            return;
        while (_compositedScreenshots.TryDequeue(out var request))
            CompleteCompositedScreenshotFailure(request.Callback, reason);
    }

    private static void CompleteCompositedScreenshotFailure(Action<ScreenshotReadbackResult> callback, string reason)
    {
        try
        {
            callback(ScreenshotReadbackResult.Failure(reason, nameof(VulkanRenderer)));
        }
        catch (Exception exception)
        {
            // A diagnostic consumer must not interrupt presentation or renderer teardown.
            Debug.VulkanWarning("[Vulkan] Composited screenshot failure callback threw: {0}", exception.Message);
        }
    }
}

using Silk.NET.Vulkan;
using XREngine.Data.Geometry;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>Observational window captures made before acquired-image ownership passes to WSI.</summary>
internal sealed partial class VulkanFrameLoop
{
    private Queue<(BoundingRectangle Region, Action<ScreenshotReadbackResult> Callback)>? _compositedScreenshots;
    private Fence _pendingCompositedScreenshotFence;
    private Semaphore[]? _compositedPresentSemaphores;

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

    private void CaptureCompositedScreenshotsBeforePresent(ref VulkanFrameAttempt attempt)
    {
        // Recovery may retry presentation after a host-side wait error. Any legacy synchronous
        // copy still owns the acquired image even though its request was dequeued.
        CompletePendingCompositedScreenshotCopy();
        if (_compositedScreenshots is not { Count: > 0 })
            return;
        // A recovery submission can present retained desktop content. Its accepted
        // timeline signal provides the same readback boundary as a normal frame.
        if ((!attempt.Submitted && !attempt.RecoverySubmissionAccepted) || attempt.GraphicsSignalValue == 0 ||
            OutputRuntime.Desktop.Images is not { } images || attempt.ImageIndex >= images.Length)
        {
            FailPendingCompositedScreenshots("No accepted desktop submission is available for window capture.");
            return;
        }

        BlitImageInfo source = new(
            images[attempt.ImageIndex],
            OutputRuntime.Desktop.ImageFormat,
            ImageAspectFlags.ColorBit, 0, 1, 0,
            OutputRuntime.Desktop.Extent,
            ImageLayout.PresentSrcKhr,
            PipelineStageFlags.AllCommandsBit,
            AccessFlags.MemoryWriteBit | AccessFlags.MemoryReadBit,
            usage: ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);

        // Fast asynchronous GPU-chained path: chain the swapchain-to-staging copy between the
        // frame's submission signal semaphore and WSI present using one per-swapchain-image binary
        // semaphore. Neither WaitForTimelineValue nor vkWaitForFences runs on the CPU, and
        // vkQueuePresentKHR waits on the chained semaphore so swapchain ownership remains valid.
        if (attempt.PresentSemaphore.Handle != 0 &&
            TryEnsureCompositedPresentSemaphore(attempt.ImageIndex, images.Length, out Semaphore chainedSemaphore))
        {
            if (!_compositedScreenshots.TryDequeue(out var request))
                return;

            if (TryQueueScreenshotReadbackCore(
                    request.Region,
                    false,
                    request.Callback,
                    out string? failure,
                    source,
                    waitSemaphore: attempt.PresentSemaphore,
                    signalSemaphore: chainedSemaphore))
            {
                attempt.CompositedScreenshotChainedPresentSemaphore = chainedSemaphore;
                attempt.PresentSemaphore = chainedSemaphore;
                attempt.ExpectedPresentWaitSemaphore = chainedSemaphore;
            }
            else
            {
                CompleteCompositedScreenshotFailure(request.Callback, failure ?? "Composited capture failed.");
            }

            if (_deviceLost)
                throw new InvalidOperationException("The Vulkan device was lost during composited screenshot readback.");
            return;
        }

        // Synchronous fallback when no binary present semaphore is available for GPU chaining.
        WaitForTimelineValue(_commandRuntime.Synchronization._graphicsTimelineSemaphore, attempt.GraphicsSignalValue);
        while (_compositedScreenshots.TryDequeue(out var request))
        {
            if (!TryQueueScreenshotReadbackCore(request.Region, false, request.Callback, out string? failure, source))
                CompleteCompositedScreenshotFailure(request.Callback, failure ?? "Composited capture failed.");
            if (_deviceLost)
                throw new InvalidOperationException("The Vulkan device was lost during composited screenshot readback.");
        }
    }

    private unsafe bool TryEnsureCompositedPresentSemaphore(uint imageIndex, int imageCount, out Semaphore semaphore)
    {
        semaphore = default;
        if (Api is null || _deviceContext.Device.Handle == 0 || imageCount <= 0 || imageIndex >= (uint)imageCount)
            return false;

        if (_compositedPresentSemaphores is null || _compositedPresentSemaphores.Length < imageCount)
        {
            Semaphore[] resized = new Semaphore[imageCount];
            if (_compositedPresentSemaphores is not null)
                Array.Copy(_compositedPresentSemaphores, resized, _compositedPresentSemaphores.Length);
            _compositedPresentSemaphores = resized;
        }

        if (_compositedPresentSemaphores[imageIndex].Handle == 0)
        {
            SemaphoreCreateInfo createInfo = new() { SType = StructureType.SemaphoreCreateInfo };
            Result result = Api.CreateSemaphore(_deviceContext.Device, in createInfo, null, out Semaphore created);
            if (result != Result.Success || created.Handle == 0)
                return false;
            _compositedPresentSemaphores[imageIndex] = created;
        }

        semaphore = _compositedPresentSemaphores[imageIndex];
        return semaphore.Handle != 0;
    }

    private unsafe void DestroyCompositedPresentSemaphores()
    {
        Semaphore[]? semaphores = _compositedPresentSemaphores;
        _compositedPresentSemaphores = null;
        if (semaphores is null || Api is null || _deviceContext.Device.Handle == 0)
            return;

        for (int i = 0; i < semaphores.Length; i++)
        {
            if (semaphores[i].Handle != 0)
                Api.DestroySemaphore(_deviceContext.Device, semaphores[i], null);
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

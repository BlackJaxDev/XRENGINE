using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Acquire and present for desktop generations on the DXGI flip-model path. Both
/// operations submit to the graphics queue under the same admission gate and queue
/// lease as the native <c>vkAcquireNextImageKHR</c> and <c>vkQueuePresentKHR</c> calls.
/// </summary>
internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Acquires the next ring image. The result codes follow the native acquire, so
    /// the existing acquire policy handles them. A flip-model swapchain never reports
    /// an out-of-date state; the frame loop's own framebuffer-size check schedules
    /// recreation after a resize.
    /// </summary>
    private Result AcquireDesktopDxgiImage(
        VulkanDxgiImageRing ring,
        ulong timeoutNanoseconds,
        Semaphore acquireSemaphore,
        ref uint imageIndex)
    {
        if (!TryAdmitVulkanDeviceOperation("DxgiPresent.Acquire", out _))
            return Result.ErrorDeviceLost;

        // Wait for the image before the queue lease, so the wait does not block
        // other queue submissions.
        Result result = ring.WaitForNextImage(timeoutNanoseconds, out imageIndex);
        if (result != Result.Success)
            return result;

        _commandRuntime.CommandBuffers.DeviceQueueAdmissionGate.EnterReadLock();
        try
        {
            using VulkanQueueOperationLease queueOperation =
                VulkanQueueOperationLease.TryEnter(
                    _oneTimeSubmitLock,
                    _deviceContext.StateMachine,
                    _frameTelemetry);
            if (!queueOperation.Acquired)
                return Result.ErrorDeviceLost;

            result = ring.SubmitAcquire(
                _deviceContext.GraphicsQueue,
                imageIndex,
                acquireSemaphore);
        }
        finally
        {
            _commandRuntime.CommandBuffers.DeviceQueueAdmissionGate.ExitReadLock();
        }

        _deviceContext.ObserveNativeResult("DxgiPresent.Acquire", result);
        RecordQueueOperation("dxgi-acquire", _deviceContext.GraphicsQueue, result, "AcquireDesktopDxgiImage");
        return result;
    }

    /// <summary>
    /// Dispatches one present on the DXGI path. The caller already holds the queue
    /// lease. The present fence signals when the Vulkan release submission completes.
    /// </summary>
    private unsafe bool TryDispatchDesktopDxgiPresent(
        VulkanDxgiImageRing ring,
        ref PresentInfoKHR presentInfo,
        Fence presentFence,
        out Result result)
    {
        if (presentInfo.WaitSemaphoreCount != 1 || presentInfo.PImageIndices is null)
            throw new InvalidOperationException("A DXGI desktop present needs exactly one wait semaphore and one image index.");

        result = ring.Present(
            _deviceContext.GraphicsQueue,
            presentInfo.PImageIndices[0],
            presentInfo.PWaitSemaphores[0],
            presentFence,
            OutputRuntime.Desktop.DxgiSyncInterval,
            OutputRuntime.Desktop.DxgiAllowTearing,
            out bool submitted);
        return submitted;
    }
}

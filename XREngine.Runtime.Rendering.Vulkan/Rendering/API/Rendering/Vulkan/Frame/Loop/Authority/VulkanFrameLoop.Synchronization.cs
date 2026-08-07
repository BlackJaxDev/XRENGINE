using System.Diagnostics;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanFrameLoop
{
    private const ulong TimelineWaitPollTimeoutNanoseconds = 50_000_000UL;

    private InvalidOperationException CreateDeviceLostException(
        string operation,
        Result result)
        => _deviceLossCoordinator.CreateException(operation, result);

    private void CompleteMappedFrameArenaDeviceLossObservation()
    {
        DeviceBootstrap.VulkanNativeDeviceFault? fault =
            _deviceContext.FirstNativeDeviceFault;
        if (fault is null)
            return;

        _deviceLossCoordinator.MarkDeviceLost(
            $"Mapped frame arena {fault.Operation} returned {fault.Result}",
            fault.Operation,
            fault.Result);
    }

    private bool TryAdmitVulkanDeviceOperation(
        string operation,
        out string failureReason)
    {
        if (_deviceContext.IsOperational)
        {
            failureReason = string.Empty;
            return true;
        }

        failureReason =
            $"Cannot start Vulkan operation '{operation}' while device state is {_deviceContext.State}.";
        return false;
    }

    private void ThrowIfVulkanDeviceOperationNotAdmitted(string operation)
    {
        if (!TryAdmitVulkanDeviceOperation(operation, out string failureReason))
            throw new InvalidOperationException(failureReason);
    }

    private bool HasTimelineValueCompleted(Semaphore semaphore, ulong value)
    {
        if (!TryAdmitVulkanDeviceOperation(nameof(HasTimelineValueCompleted), out _))
            return false;
        if (semaphore.Handle == 0 || value == 0)
            return true;
        if (value == ulong.MaxValue)
            throw new InvalidOperationException(
                "Refusing to query Vulkan timeline semaphore completion for the invalid ulong.MaxValue sentinel.");

        Result result = _commandRuntime.Synchronization.QueryTimelineCompletion(
            Api,
            _deviceContext,
            _resourceRuntime.Lifetime.Tracker,
            semaphore,
            value,
            out bool completed);
        if (result == Result.ErrorDeviceLost)
            throw CreateDeviceLostException("vkGetSemaphoreCounterValue", result);
        if (result != Result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to query timeline semaphore value {value}. Result={result}.");
        }

        return completed;
    }

    private bool TryWaitForTimelineValue(
        Semaphore semaphore,
        ulong value,
        ulong timeoutNanoseconds)
    {
        if (!TryAdmitVulkanDeviceOperation(nameof(TryWaitForTimelineValue), out _))
            return false;
        if (semaphore.Handle == 0 || value == 0)
            return true;
        if (value == ulong.MaxValue)
            throw new InvalidOperationException(
                "Refusing to wait for the invalid Vulkan timeline semaphore value ulong.MaxValue.");

        Result result = _commandRuntime.Synchronization.WaitForTimelineCompletion(
            Api,
            _deviceContext,
            _resourceRuntime.Lifetime.Tracker,
            semaphore,
            value,
            timeoutNanoseconds);
        if (result == Result.Success)
            return true;
        if (result == Result.Timeout)
            return false;
        if (result == Result.ErrorDeviceLost)
            throw CreateDeviceLostException("vkWaitSemaphores", result);

        throw new InvalidOperationException(
            $"Failed to wait for timeline semaphore value {value}. Result={result}.");
    }

    private void WaitForTimelineValue(Semaphore semaphore, ulong value)
    {
        long waitStart = Stopwatch.GetTimestamp();
        while (!TryWaitForTimelineValue(
                   semaphore,
                   value,
                   TimelineWaitPollTimeoutNanoseconds))
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.TimelineWait.{GetHashCode()}.{semaphore.Handle:X}.{value}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Still waiting for timeline semaphore 0x{0:X} to reach value {1}. WaitedMs={2:F1}",
                semaphore.Handle,
                value,
                Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds);
        }
    }
}
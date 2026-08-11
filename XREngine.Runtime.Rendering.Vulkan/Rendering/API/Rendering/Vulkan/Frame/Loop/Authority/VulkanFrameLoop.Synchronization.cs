using System.Diagnostics;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanFrameLoop
{
    private const ulong TimelineWaitPollTimeoutNanoseconds = 50_000_000UL;

    private void CompleteMappedFrameArenaDeviceLossObservation()
    {
        DeviceBootstrap.VulkanNativeDeviceFault? fault =
            _deviceContext.FirstNativeDeviceFault;
        if (fault is null)
            return;

        MarkDeviceLost(
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

    internal bool HasTimelineValueCompleted(Semaphore semaphore, ulong value)
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

    internal void WaitForTimelineValue(Semaphore semaphore, ulong value)
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

    internal void CreateSyncObjects()
    {
        if (!_deviceContext.Capabilities.Supports(EVulkanDeviceCapability.TimelineSemaphores))
            throw new InvalidOperationException("Vulkan timeline semaphores are required but were not enabled on the logical device.");

        VulkanCommandSynchronizationState sync = _commandRuntime.Synchronization;
        sync.acquireBridgeSemaphores = new Semaphore[FrameSlotCount];
        sync._frameSlotTimelineValues = new ulong[FrameSlotCount];
        EnsureSwapchainTimelineState();
        SemaphoreTypeCreateInfo type = new() { SType = StructureType.SemaphoreTypeCreateInfo, SemaphoreType = SemaphoreType.Timeline };
        SemaphoreCreateInfo timeline = new() { SType = StructureType.SemaphoreCreateInfo, PNext = &type };
        if (Api.CreateSemaphore(_deviceContext.Device, ref timeline, null, out sync._graphicsTimelineSemaphore) != Result.Success ||
            Api.CreateSemaphore(_deviceContext.Device, ref timeline, null, out sync._presentTimelineSemaphore) != Result.Success ||
            Api.CreateSemaphore(_deviceContext.Device, ref timeline, null, out sync._transferTimelineSemaphore) != Result.Success)
            throw new InvalidOperationException("Failed to create Vulkan timeline synchronization semaphores.");

        SemaphoreCreateInfo binary = new() { SType = StructureType.SemaphoreCreateInfo };
        for (int index = 0; index < FrameSlotCount; index++)
            if (Api.CreateSemaphore(_deviceContext.Device, ref binary, null, out sync.acquireBridgeSemaphores[index]) != Result.Success)
                throw new InvalidOperationException("Failed to create Vulkan acquire bridge synchronization semaphores.");

        if (_targetDriver.RequiresSwapchainOutput)
            CreateDesktopPresentBridgeSemaphores(_outputRuntime.Desktop.Images?.Length ?? FrameSlotCount);
    }

    internal void DestroySyncObjects()
    {
        VulkanCommandSynchronizationState sync = _commandRuntime.Synchronization;
        sync.FailAllSubmissionMarkers();
        if (sync.acquireBridgeSemaphores is not null)
            for (int index = 0; index < sync.acquireBridgeSemaphores.Length; index++)
                Api.DestroySemaphore(_deviceContext.Device, sync.acquireBridgeSemaphores[index], null);
        if (_outputRuntime.Desktop.PresentBridgeSemaphores is not null && _targetDriver.RequiresSwapchainOutput)
            DestroyDesktopPresentBridgeSemaphores();
        if (sync._graphicsTimelineSemaphore.Handle != 0) Api.DestroySemaphore(_deviceContext.Device, sync._graphicsTimelineSemaphore, null);
        if (sync._presentTimelineSemaphore.Handle != 0) Api.DestroySemaphore(_deviceContext.Device, sync._presentTimelineSemaphore, null);
        if (sync._transferTimelineSemaphore.Handle != 0) Api.DestroySemaphore(_deviceContext.Device, sync._transferTimelineSemaphore, null);
        sync.acquireBridgeSemaphores = null;
        sync._graphicsTimelineSemaphore = default;
        sync._presentTimelineSemaphore = default;
        sync._transferTimelineSemaphore = default;
        sync._frameSlotTimelineValues = null;
        sync._desktopImageTimelineValues = null;
        _outputRuntime.Desktop.ImageTimelineValues = null;
        sync._acquireTimelineValue = 0;
        sync._graphicsTimelineValue = 0;
    }

    private void EnsureSwapchainTimelineState()
    {
        if (_outputRuntime.Desktop.Images is null)
        {
            _outputRuntime.Desktop.ImageTimelineValues = null;
            _commandRuntime.Synchronization._desktopImageTimelineValues = null;
            return;
        }
        if (_outputRuntime.Desktop.ImageTimelineValues is null || _outputRuntime.Desktop.ImageTimelineValues.Length != _outputRuntime.Desktop.Images.Length)
            _outputRuntime.Desktop.ImageTimelineValues = new ulong[_outputRuntime.Desktop.Images.Length];
        else Array.Clear(_outputRuntime.Desktop.ImageTimelineValues);
        _commandRuntime.Synchronization._desktopImageTimelineValues =
            _outputRuntime.Desktop.ImageTimelineValues;
    }
}

using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private Semaphore[]? acquireBridgeSemaphores;
    /// <summary>
    /// Present bridge semaphores indexed by swapchain image index (one per swapchain image).
    /// This prevents signaling a semaphore that may still be in use by a previously presented image.
    /// </summary>
    private Semaphore[]? presentBridgeSemaphores;
    private Semaphore _graphicsTimelineSemaphore;
    private Semaphore _presentTimelineSemaphore;
    private Semaphore _transferTimelineSemaphore;
    private ulong[]? _frameSlotTimelineValues;
    private ulong[]? _swapchainImageTimelineValues;
    private ulong _acquireTimelineValue;
    private ulong _graphicsTimelineValue;
    private const ulong TimelineWaitPollTimeoutNanoseconds = 50_000_000UL;

    /// <summary>
    /// Set to <c>true</c> when <c>VK_ERROR_DEVICE_LOST</c> is detected. Once the Vulkan
    /// logical device is lost it cannot be recovered — all subsequent API calls will fail.
    /// The render loop checks this flag to short-circuit immediately instead of looping
    /// forever with cascading failures.
    /// </summary>
    private volatile bool _deviceLost;
    private string? _deviceLostReason;
    public override bool IsDeviceLost => _deviceLost;
    public override string? DeviceLostReason => _deviceLostReason;

    private void MarkDeviceLost(string? reason = null)
    {
        RecordFirstFailingVulkanApi(reason);

        bool firstObservation;
        string deviceLostReason;
        lock (_deviceLostTransitionLock)
        {
            firstObservation = !_deviceLost;
            if (firstObservation)
            {
                _deviceLost = true;
                _deviceLostReason = BuildDeviceLostReasonWithSubmissionContext(reason);

                // Device loss means pending timeline signals will never arrive. Clear
                // them so teardown/recovery paths do not block on dead semaphore values.
                if (_frameSlotTimelineValues is not null)
                    Array.Clear(_frameSlotTimelineValues);
                if (_swapchainImageTimelineValues is not null)
                    Array.Clear(_swapchainImageTimelineValues);
                _acquireTimelineValue = 0;
                _graphicsTimelineValue = 0;
            }
            else if (!string.IsNullOrWhiteSpace(reason) && string.IsNullOrWhiteSpace(_deviceLostReason))
            {
                _deviceLostReason = BuildDeviceLostReasonWithSubmissionContext(reason);
            }

            deviceLostReason = string.IsNullOrWhiteSpace(_deviceLostReason)
                ? "<unknown>"
                : _deviceLostReason;
        }

        if (firstObservation)
        {
            Debug.VulkanWarning(
                "[Vulkan] Logical device lost. Reason={0}. The current Vulkan renderer cannot submit more work; recreate the renderer/window to recover.",
                deviceLostReason);
        }
    }

    private InvalidOperationException CreateDeviceLostException(string operation, Result result)
    {
        MarkDeviceLost($"{operation} returned {result}");
        return new InvalidOperationException(
            $"Vulkan device lost during {operation} ({result}). Reason={DeviceLostReason ?? "<unknown>"}. The logical device is terminal and the renderer/window must be recreated before Vulkan can render again.");
    }

    private void EnsureSwapchainTimelineState()
    {
        if (swapChainImages is null)
        {
            _swapchainImageTimelineValues = null;
            return;
        }

        if (_swapchainImageTimelineValues is null || _swapchainImageTimelineValues.Length != swapChainImages.Length)
            _swapchainImageTimelineValues = new ulong[swapChainImages.Length];
        else
            Array.Clear(_swapchainImageTimelineValues, 0, _swapchainImageTimelineValues.Length);
    }

    private bool HasTimelineValueCompleted(Semaphore semaphore, ulong value)
    {
        if (semaphore.Handle == 0 || value == 0)
            return true;

        if (value == ulong.MaxValue)
            throw new InvalidOperationException("Refusing to query Vulkan timeline semaphore completion for the invalid ulong.MaxValue sentinel.");

        ulong currentValue = 0;
        Result result = Api!.GetSemaphoreCounterValue(device, semaphore, &currentValue);
        if (result == Result.ErrorDeviceLost)
        {
            MarkDeviceLost($"GetSemaphoreCounterValue for timeline value {value} returned {result}");

            throw new InvalidOperationException(
                $"Vulkan device lost while checking timeline value {value}. Timeline state has been reset.");
        }

        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to query timeline semaphore value {value}. Result={result}.");

        return currentValue >= value;
    }

    private bool TryWaitForTimelineValue(Semaphore semaphore, ulong value, ulong timeoutNanoseconds)
    {
        if (semaphore.Handle == 0 || value == 0)
            return true;

        if (value == ulong.MaxValue)
            throw new InvalidOperationException("Refusing to wait for the invalid Vulkan timeline semaphore value ulong.MaxValue.");

        SemaphoreWaitInfo waitInfo = new()
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
        };

        Semaphore* semaphorePtr = stackalloc Semaphore[1];
        ulong* valuePtr = stackalloc ulong[1];
        semaphorePtr[0] = semaphore;
        valuePtr[0] = value;
        waitInfo.PSemaphores = semaphorePtr;
        waitInfo.PValues = valuePtr;

        Result waitResult = Api!.WaitSemaphores(device, &waitInfo, timeoutNanoseconds);
        if (waitResult == Result.Success)
            return true;

        if (waitResult == Result.Timeout)
            return false;

        if (waitResult == Result.ErrorDeviceLost)
        {
            MarkDeviceLost($"WaitSemaphores for timeline value {value} returned {waitResult}");

            throw new InvalidOperationException(
                $"Vulkan device lost while waiting for timeline value {value}. Timeline state has been reset.");
        }

        if (waitResult != Result.Success)
            throw new InvalidOperationException($"Failed to wait for timeline semaphore value {value}. Result={waitResult}.");

        return true;
    }

    private void WaitForTimelineValue(Semaphore semaphore, ulong value)
    {
        long waitStart = Stopwatch.GetTimestamp();
        while (!TryWaitForTimelineValue(semaphore, value, TimelineWaitPollTimeoutNanoseconds))
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

    private void DestroySyncObjects()
    {
        if (acquireBridgeSemaphores is not null)
        {
            for (int i = 0; i < acquireBridgeSemaphores.Length; i++)
                Api!.DestroySemaphore(device, acquireBridgeSemaphores[i], null);
        }

        if (presentBridgeSemaphores is not null)
        {
            for (int i = 0; i < presentBridgeSemaphores.Length; i++)
                Api!.DestroySemaphore(device, presentBridgeSemaphores[i], null);
        }

        if (_graphicsTimelineSemaphore.Handle != 0)
            Api!.DestroySemaphore(device, _graphicsTimelineSemaphore, null);
        if (_presentTimelineSemaphore.Handle != 0)
            Api!.DestroySemaphore(device, _presentTimelineSemaphore, null);
        if (_transferTimelineSemaphore.Handle != 0)
            Api!.DestroySemaphore(device, _transferTimelineSemaphore, null);

        acquireBridgeSemaphores = null;
        presentBridgeSemaphores = null;
        _graphicsTimelineSemaphore = default;
        _presentTimelineSemaphore = default;
        _transferTimelineSemaphore = default;
        _frameSlotTimelineValues = null;
        _swapchainImageTimelineValues = null;
        _acquireTimelineValue = 0;
        _graphicsTimelineValue = 0;
    }

    private void CreateSyncObjects()
    {
        if (!_supportsTimelineSemaphores)
            throw new InvalidOperationException("Vulkan timeline semaphores are required but were not enabled on the logical device.");

        acquireBridgeSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        int presentSemaphoreCount = swapChainImages?.Length ?? MAX_FRAMES_IN_FLIGHT;
        presentBridgeSemaphores = new Semaphore[presentSemaphoreCount];
        _frameSlotTimelineValues = new ulong[MAX_FRAMES_IN_FLIGHT];
        EnsureSwapchainTimelineState();

        SemaphoreCreateInfo semaphoreInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
        };

        SemaphoreTypeCreateInfo timelineTypeInfo = new()
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };

        SemaphoreCreateInfo timelineSemaphoreInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &timelineTypeInfo,
        };

        if (Api!.CreateSemaphore(device, ref timelineSemaphoreInfo, null, out _graphicsTimelineSemaphore) != Result.Success ||
            Api.CreateSemaphore(device, ref timelineSemaphoreInfo, null, out _presentTimelineSemaphore) != Result.Success ||
            Api.CreateSemaphore(device, ref timelineSemaphoreInfo, null, out _transferTimelineSemaphore) != Result.Success)
        {
            throw new Exception("failed to create timeline synchronization semaphores.");
        }

        SetDebugObjectName(ObjectType.Semaphore, _graphicsTimelineSemaphore.Handle, "Timeline.Graphics");
        SetDebugObjectName(ObjectType.Semaphore, _presentTimelineSemaphore.Handle, "Timeline.Present");
        SetDebugObjectName(ObjectType.Semaphore, _transferTimelineSemaphore.Handle, "Timeline.Transfer");

        for (var i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            if (Api!.CreateSemaphore(device, ref semaphoreInfo, null, out acquireBridgeSemaphores[i]) != Result.Success)
            {
                throw new Exception("failed to create acquire bridge synchronization semaphores.");
            }

            SetDebugObjectName(ObjectType.Semaphore, acquireBridgeSemaphores[i].Handle, $"AcquireBridge[{i}]");
        }

        for (var i = 0; i < presentSemaphoreCount; i++)
        {
            if (Api!.CreateSemaphore(device, ref semaphoreInfo, null, out presentBridgeSemaphores[i]) != Result.Success)
            {
                throw new Exception("failed to create frame bridge synchronization semaphores.");
            }

            SetDebugObjectName(ObjectType.Semaphore, presentBridgeSemaphores[i].Handle, $"PresentBridge[{i}]");
        }
    }
}

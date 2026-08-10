using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Present bridge semaphores indexed by swapchain image index (one per swapchain image).
    /// This prevents signaling a semaphore that may still be in use by a previously presented image.
    /// </summary>
    private const ulong TimelineWaitPollTimeoutNanoseconds = 50_000_000UL;

    /// <summary>
    /// Set to <c>true</c> when <c>VK_ERROR_DEVICE_LOST</c> is detected. Once the Vulkan
    /// logical device is lost it cannot be recovered â€” all subsequent API calls will fail.
    /// The render loop checks this flag to short-circuit immediately instead of looping
    /// forever with cascading failures.
    /// </summary>
    // All legacy field-style checks read the atomic device-state authority. This
    // closes the admission window between the winning loss CAS and diagnostic
    // collection without requiring every hot path to take the transition lock.
    private bool _deviceLost => !_deviceContext.StateMachine.IsOperational;
    public override bool IsDeviceLost => _deviceLost;
    public override string? DeviceLostReason => _deviceContext.DeviceFaultFacility.DeviceLostReason;
    /// <summary>
    /// Whether a live logical device exists and has not entered a terminal fault state.
    /// The state machine starts healthy so it can collect a later device loss, but that
    /// state alone must not make re-entrant startup rendering treat a null device as ready.
    /// </summary>
    /// <summary>
    /// Admits a native device operation only while the logical device is healthy.
    /// New recording, submission, waiting, allocation, mapping, descriptor update,
    /// and publication paths must use this single state authority.
    /// </summary>
    internal bool TryAdmitVulkanDeviceOperation(string operation, out string failureReason)
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

    /// <summary>
    /// Throws when a caller attempts to begin a native operation after admission
    /// has closed because the device is unavailable or terminal.
    /// </summary>
    internal void ThrowIfVulkanDeviceOperationNotAdmitted(string operation)
    {
        if (!TryAdmitVulkanDeviceOperation(operation, out string failureReason))
            throw new InvalidOperationException(failureReason);
    }

    internal void MarkDeviceLost(
        string? reason = null,
        string? operation = null,
        Result result = Result.ErrorDeviceLost)
    {
        DeviceBootstrap.VulkanNativeDeviceFault? nativeFault =
            _deviceContext.FirstNativeDeviceFault;
        operation ??= nativeFault?.Operation;
        if (nativeFault is not null && result == Result.ErrorDeviceLost)
            result = nativeFault.Result;
        reason ??= nativeFault is null
            ? null
            : $"{nativeFault.Operation} returned {nativeFault.Result}";

        bool firstObservation;
        lock (_oneTimeSubmitLock)
        {
            lock (_frameTelemetry._deviceLostTransitionLock)
            {
                _ = _deviceContext.TryBeginDeviceLossCollection();
                firstObservation =
                    _deviceContext.TryClaimDeviceLossDiagnostics();
                if (firstObservation)
                {
                    CaptureFirstDeviceLossRecord(operation, result, reason);
                    FailAllSubmissionMarkers();
                    NotifyVulkanResourceLifetimeDeviceLost();

                    // Pending timeline signals will never arrive after device loss.
                    if (_commandRuntime.Synchronization._frameSlotTimelineValues is not null)
                        Array.Clear(_commandRuntime.Synchronization._frameSlotTimelineValues);
                    if (OutputRuntime.Desktop.ImageTimelineValues is not null)
                        Array.Clear(OutputRuntime.Desktop.ImageTimelineValues);
                    _commandRuntime.Synchronization._acquireTimelineValue = 0;
                    _commandRuntime.Synchronization._graphicsTimelineValue = 0;
                }
                else
                {
                    _deviceContext.DeviceFaultFacility.RecordDeviceLossFallout();
                }
            }
        }

        if (!firstObservation)
            return;

        string deviceLostReason = BuildDeviceLostReasonWithSubmissionContext(reason);
        lock (_frameTelemetry._deviceLostTransitionLock)
        {
            _deviceContext.DeviceFaultFacility.CompleteDeviceLoss(deviceLostReason);
            _deviceContext.CompleteDeviceLossCollection();
        }

        Debug.VulkanWarning(
            "[Vulkan] Logical device lost. Reason={0}. The current Vulkan renderer cannot submit more work; recreate the renderer/window to recover.",
            deviceLostReason);

        // Device-loss observation may stop the normal frame poll immediately. Complete
        // screenshot consumers now so MCP sessions cannot remain stuck waiting on fences
        // that Vulkan guarantees will never signal after logical-device loss.
        OutputRuntime.Capture.FailPendingScreenshotReadbacksForDeviceLoss(deviceLostReason);
    }

    private void MarkDeviceDisposed()
    {
        lock (_oneTimeSubmitLock)
            _deviceContext.MarkDisposed();
    }

    internal InvalidOperationException CreateDeviceLostException(string operation, Result result)
    {
        MarkDeviceLost($"{operation} returned {result}", operation, result);
        return new InvalidOperationException(
            $"Vulkan device lost during {operation} ({result}). Reason={DeviceLostReason ?? "<unknown>"}. The logical device is terminal and the renderer/window must be recreated before Vulkan can render again.");
    }

    /// <summary>
    /// Captures original failure evidence before loss fallout clears timeline
    /// state. Only the device-state CAS winner reaches this method.
    /// </summary>
    private void CaptureFirstDeviceLossRecord(
        string? operation,
        Result result,
        string? reason)
    {
        string? provisionalOperation = Volatile.Read(ref _frameTelemetry._firstFailingVulkanApi);
        string resolvedOperation = !string.IsNullOrWhiteSpace(operation)
            ? operation
            : !string.IsNullOrWhiteSpace(provisionalOperation)
                ? provisionalOperation
                : !string.IsNullOrWhiteSpace(reason)
                    ? reason
                    : "<unknown>";
        string resolvedReason = string.IsNullOrWhiteSpace(reason)
            ? "<unknown>"
            : reason;
        // Earlier operation sites may have observed an error concurrently, but
        // only this state-transition winner defines the terminal device-loss
        // diagnosis. Replace the provisional marker with the CAS-owned record.
        Interlocked.Exchange(ref _frameTelemetry._firstFailingVulkanApi, $"{resolvedOperation}:{result}");

        VulkanDeviceLossRecord record = new(
            resolvedOperation,
            result,
            resolvedReason,
            DateTimeOffset.UtcNow,
            SnapshotLastVulkanSubmissionDiagnosticContext(),
            GetVulkanResourceLifetimeSnapshot(includeExactLiveResourceGenerations: true));
        _ = Interlocked.CompareExchange(ref _frameTelemetry._firstDeviceLossRecord, record, null);
    }

    private void EnsureSwapchainTimelineState()
    {
        SettleMappedFrameArenaSlotsBeforeResettingSwapchainTimelineState(
            OutputRuntime.Desktop.ImageTimelineValues);

        if (OutputRuntime.Desktop.Images is null)
        {
            OutputRuntime.Desktop.ImageTimelineValues = null;
            return;
        }

        if (OutputRuntime.Desktop.ImageTimelineValues is null || OutputRuntime.Desktop.ImageTimelineValues.Length != OutputRuntime.Desktop.Images.Length)
            OutputRuntime.Desktop.ImageTimelineValues = new ulong[OutputRuntime.Desktop.Images.Length];
        else
            Array.Clear(OutputRuntime.Desktop.ImageTimelineValues, 0, OutputRuntime.Desktop.ImageTimelineValues.Length);
    }

    /// <summary>
    /// Preserves the completion proof for desktop image-indexed frame-data chunks while a
    /// swapchain is recreated. The new swapchain starts with no image ownership, but the
    /// mapped arena may still have a submitted chunk for the retired image at that index.
    /// </summary>
    private void SettleMappedFrameArenaSlotsBeforeResettingSwapchainTimelineState(
        ulong[]? imageTimelineValues)
    {
        if (imageTimelineValues is null || MappedFrameArena is not { } arena)
            return;

        ulong generation = arena.Generation;
        if (generation == 0)
            return;

        int slotCount = Math.Min(imageTimelineValues.Length, arena.FrameSlotCount);
        for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
        {
            ulong completionValue = imageTimelineValues[slotIndex];
            if (completionValue == 0)
                continue;

            WaitForTimelineValue(
                _commandRuntime.Synchronization._graphicsTimelineSemaphore,
                completionValue);
            if (!arena.TryResetFrameSlot(
                    unchecked((uint)slotIndex),
                    generation,
                    submissionCompletionProven: true))
            {
                throw new InvalidOperationException(
                    $"Mapped frame-data slot {slotIndex} could not be settled before swapchain timeline state was reset.");
            }
        }
    }

    private bool HasTimelineValueCompleted(Semaphore semaphore, ulong value)
    {
        if (!TryAdmitVulkanDeviceOperation(nameof(HasTimelineValueCompleted), out _))
            return false;

        if (semaphore.Handle == 0 || value == 0)
            return true;

        if (value == ulong.MaxValue)
            throw new InvalidOperationException("Refusing to query Vulkan timeline semaphore completion for the invalid ulong.MaxValue sentinel.");

        ulong currentValue = 0;
        Result result = Api!.GetSemaphoreCounterValue(_deviceContext.Device, semaphore, &currentValue);
        if (result == Result.ErrorDeviceLost)
        {
            MarkDeviceLost(
                $"GetSemaphoreCounterValue for timeline value {value} returned {result}",
                "vkGetSemaphoreCounterValue",
                result);

            throw new InvalidOperationException(
                $"Vulkan device lost while checking timeline value {value}. Reason={DeviceLostReason ?? "<unknown>"}. Timeline state has been reset.");
        }

        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to query timeline semaphore value {value}. Result={result}.");

        bool completed = currentValue >= value;
        if (completed)
            NotifyVulkanTimelineCompleted(semaphore, currentValue);
        return completed;
    }

    private bool TryWaitForTimelineValue(Semaphore semaphore, ulong value, ulong timeoutNanoseconds)
    {
        if (!TryAdmitVulkanDeviceOperation(nameof(TryWaitForTimelineValue), out _))
            return false;

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

        Result waitResult = Api!.WaitSemaphores(_deviceContext.Device, &waitInfo, timeoutNanoseconds);
        if (waitResult == Result.Success)
        {
            NotifyVulkanTimelineCompleted(semaphore, value);
            return true;
        }

        if (waitResult == Result.Timeout)
            return false;

        if (waitResult == Result.ErrorDeviceLost)
        {
            MarkDeviceLost(
                $"WaitSemaphores for timeline value {value} returned {waitResult}",
                "vkWaitSemaphores",
                waitResult);

            throw new InvalidOperationException(
                $"Vulkan device lost while waiting for timeline value {value}. Reason={DeviceLostReason ?? "<unknown>"}. Timeline state has been reset.");
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
        FailAllSubmissionMarkers();
        if (_commandRuntime.Synchronization.acquireBridgeSemaphores is not null)
        {
            for (int i = 0; i < _commandRuntime.Synchronization.acquireBridgeSemaphores.Length; i++)
                Api!.DestroySemaphore(_deviceContext.Device, _commandRuntime.Synchronization.acquireBridgeSemaphores[i], null);
        }

        if (OutputRuntime.Desktop.PresentBridgeSemaphores is not null &&
            OutputRuntime.TargetDriver.RequiresSwapchainOutput)
            OutputRuntime.DestroyDesktopPresentBridgeSemaphores();

        if (_commandRuntime.Synchronization._graphicsTimelineSemaphore.Handle != 0)
            Api!.DestroySemaphore(_deviceContext.Device, _commandRuntime.Synchronization._graphicsTimelineSemaphore, null);
        if (_commandRuntime.Synchronization._presentTimelineSemaphore.Handle != 0)
            Api!.DestroySemaphore(_deviceContext.Device, _commandRuntime.Synchronization._presentTimelineSemaphore, null);
        if (_commandRuntime.Synchronization._transferTimelineSemaphore.Handle != 0)
            Api!.DestroySemaphore(_deviceContext.Device, _commandRuntime.Synchronization._transferTimelineSemaphore, null);

        _commandRuntime.Synchronization.acquireBridgeSemaphores = null;
        _commandRuntime.Synchronization._graphicsTimelineSemaphore = default;
        _commandRuntime.Synchronization._presentTimelineSemaphore = default;
        _commandRuntime.Synchronization._transferTimelineSemaphore = default;
        _commandRuntime.Synchronization._frameSlotTimelineValues = null;
        OutputRuntime.Desktop.ImageTimelineValues = null;
        _commandRuntime.Synchronization._acquireTimelineValue = 0;
        _commandRuntime.Synchronization._graphicsTimelineValue = 0;
    }

    private void CreateSyncObjects()
    {
        if (!_deviceContext.Capabilities.Supports(EVulkanDeviceCapability.TimelineSemaphores))
            throw new InvalidOperationException("Vulkan timeline semaphores are required but were not enabled on the logical device.");

        _commandRuntime.Synchronization.acquireBridgeSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
        int presentSemaphoreCount = OutputRuntime.Desktop.Images?.Length ?? MAX_FRAMES_IN_FLIGHT;
        _commandRuntime.Synchronization._frameSlotTimelineValues = new ulong[MAX_FRAMES_IN_FLIGHT];
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

        if (Api!.CreateSemaphore(_deviceContext.Device, ref timelineSemaphoreInfo, null, out _commandRuntime.Synchronization._graphicsTimelineSemaphore) != Result.Success ||
            Api.CreateSemaphore(_deviceContext.Device, ref timelineSemaphoreInfo, null, out _commandRuntime.Synchronization._presentTimelineSemaphore) != Result.Success ||
            Api.CreateSemaphore(_deviceContext.Device, ref timelineSemaphoreInfo, null, out _commandRuntime.Synchronization._transferTimelineSemaphore) != Result.Success)
        {
            throw new Exception("failed to create timeline synchronization semaphores.");
        }

        SetDebugObjectName(ObjectType.Semaphore, _commandRuntime.Synchronization._graphicsTimelineSemaphore.Handle, "Timeline.Graphics");
        SetDebugObjectName(ObjectType.Semaphore, _commandRuntime.Synchronization._presentTimelineSemaphore.Handle, "Timeline.Present");
        SetDebugObjectName(ObjectType.Semaphore, _commandRuntime.Synchronization._transferTimelineSemaphore.Handle, "Timeline.Transfer");

        for (var i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
        {
            if (Api!.CreateSemaphore(_deviceContext.Device, ref semaphoreInfo, null, out _commandRuntime.Synchronization.acquireBridgeSemaphores[i]) != Result.Success)
            {
                throw new Exception("failed to create acquire bridge synchronization semaphores.");
            }

            SetDebugObjectName(ObjectType.Semaphore, _commandRuntime.Synchronization.acquireBridgeSemaphores[i].Handle, $"AcquireBridge[{i}]");
        }

        if (OutputRuntime.TargetDriver.RequiresSwapchainOutput)
            OutputRuntime.CreateDesktopPresentBridgeSemaphores(presentSemaphoreCount);
    }
}

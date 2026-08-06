using System;
using System.IO;
using System.Text;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int VulkanDeviceFaultDescriptionBytes = 256;
    private const int MaxDeviceFaultReportEntries = 32;
    private const int MaxDeviceAddressBindingReportEntries = 8;
    private const int MaxNvCheckpointReportEntries = 8;

    /// <summary>
    /// Releases the Vulkan diagnostic storage, including function pointers for device fault reporting.
    /// </summary>
    private void ReleaseVulkanDiagnosticStorage()
        => _deviceContext.ReleaseKhrDeviceFaultCommandTable();

    /// <summary>
    /// Takes a snapshot of the Vulkan command diagnostic marker for the given submit info.
    /// </summary>
    /// <param name="submitInfo">The Vulkan submit info containing the command buffers to snapshot.</param>
    /// <returns>The Vulkan command diagnostic marker corresponding to the given submit info, or the default marker if none is found.</returns>
    private VulkanCommandDiagnosticMarker SnapshotVulkanCommandDiagnosticMarker(ref SubmitInfo submitInfo)
    {
        if (submitInfo.CommandBufferCount == 0 || submitInfo.PCommandBuffers is null)
            return default;

        lock (_frameTelemetry._vulkanSubmissionDiagnosticsLock)
        {
            long latestSerial = Volatile.Read(ref _frameTelemetry._vulkanCommandDiagnosticMarkerSerial);
            int available = (int)Math.Min(latestSerial, VulkanCommandDiagnosticMarkerCapacity);
            for (long serial = latestSerial; serial > 0 && latestSerial - serial < available; serial--)
            {
                int index = unchecked((int)((serial - 1) % VulkanCommandDiagnosticMarkerCapacity));
                VulkanCommandDiagnosticMarker marker = _frameTelemetry._vulkanCommandDiagnosticMarkers[index];
                if (marker.Serial != unchecked((ulong)serial))
                    continue;

                for (uint i = 0; i < submitInfo.CommandBufferCount; i++)
                {
                    CommandBuffer submittedCommandBuffer = submitInfo.PCommandBuffers[i];
                    ulong submittedGeneration = ResolveCommandBufferRecordingGeneration(submittedCommandBuffer);
                    if (CommandDiagnosticMarkerMatchesSubmittedCommand(
                            marker.CommandBufferHandle,
                            marker.CommandBufferRecordingGeneration,
                            unchecked((ulong)submittedCommandBuffer.Handle),
                            submittedGeneration))
                    {
                        return marker;
                    }
                }
            }
        }

        return default;
    }

    /// <summary>
    /// Determines whether a Vulkan command diagnostic marker matches a submitted command buffer based on their handles and recording generations.
    /// </summary>
    /// <param name="markerHandle">The handle of the Vulkan command diagnostic marker.</param>
    /// <param name="markerGeneration">The recording generation of the Vulkan command diagnostic marker.</param>
    /// <param name="submittedHandle">The handle of the submitted Vulkan command buffer.</param>
    /// <param name="submittedGeneration">The recording generation of the submitted Vulkan command buffer.</param>
    /// <returns>True if the marker matches the submitted command buffer; otherwise, false.</returns>
    internal static bool CommandDiagnosticMarkerMatchesSubmittedCommand(
        ulong markerHandle,
        ulong markerGeneration,
        ulong submittedHandle,
        ulong submittedGeneration)
        => markerHandle == submittedHandle && markerGeneration == submittedGeneration;

    /// <summary>
    /// Records the first Vulkan API that failed, if it has not been recorded already.
    /// </summary>
    /// <param name="api">The name of the Vulkan API that failed.</param>
    private void RecordFirstFailingVulkanApi(string? api)
    {
        if (string.IsNullOrWhiteSpace(api))
            return;

        Interlocked.CompareExchange(ref _frameTelemetry._firstFailingVulkanApi, api, null);
    }

    /// <summary>
    /// Records a Vulkan image layout transition breadcrumb for diagnostic purposes.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer associated with the image layout transition.</param>
    /// <param name="imageBarrierCount">The number of image memory barriers.</param>
    /// <param name="imageBarriers">A pointer to the array of image memory barriers.</param>
    /// <param name="caller">The name of the calling method or context.</param>
    private void RecordVulkanImageLayoutTransitionBreadcrumb(
        CommandBuffer commandBuffer,
        uint imageBarrierCount,
        ImageMemoryBarrier* imageBarriers,
        string? caller)
    {
        if (!_frameTelemetry._diagnosticOptions.EnableCrashBreadcrumbs || imageBarrierCount == 0 || imageBarriers is null)
            return;

        lock (_frameTelemetry._vulkanSubmissionDiagnosticsLock)
        {
            for (uint i = 0; i < imageBarrierCount; i++)
            {
                ImageMemoryBarrier barrier = imageBarriers[i];
                long serial = Interlocked.Increment(ref _frameTelemetry._vulkanImageLayoutTransitionSerial);
                int index = unchecked((int)((serial - 1) % VulkanImageLayoutTransitionCapacity));
                _frameTelemetry._vulkanImageLayoutTransitions[index] = new(
                    unchecked((ulong)serial),
                    unchecked((ulong)commandBuffer.Handle),
                    barrier.Image.Handle,
                    barrier.SubresourceRange.AspectMask,
                    barrier.SubresourceRange.BaseMipLevel,
                    barrier.SubresourceRange.LevelCount,
                    barrier.SubresourceRange.BaseArrayLayer,
                    barrier.SubresourceRange.LayerCount,
                    barrier.OldLayout,
                    barrier.NewLayout,
                    barrier.SrcQueueFamilyIndex,
                    barrier.DstQueueFamilyIndex,
                    caller);
            }
        }
    }

    /// <summary>
    /// Records a Vulkan descriptor table generation event for diagnostic purposes.
    /// </summary>
    /// <param name="reason">The reason for recording the descriptor table generation event.</param>
    internal void RecordVulkanDescriptorTableGeneration(string reason)
    {
        if (!_frameTelemetry._diagnosticOptions.EnableCrashBreadcrumbs)
            return;

        Interlocked.Increment(ref _frameTelemetry._vulkanDescriptorTableGeneration);
    }

    /// <summary>
    /// Records a Vulkan command diagnostic marker for the specified command buffer and frame operation.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer associated with the command.</param>
    /// <param name="op">The frame operation being executed.</param>
    /// <param name="passIndex">The index of the pass within the frame operation.</param>
    /// <param name="batchIndex">The index of the batch within the pass.</param>
    internal void RecordVulkanCommandDiagnosticMarker(CommandBuffer commandBuffer, FrameOp op, int passIndex, int batchIndex)
    {
        bool wantsCrashMarker = _frameTelemetry._diagnosticOptions.EnableCrashBreadcrumbs;
        bool wantsNvCheckpoint = _frameTelemetry._diagnosticOptions.RequestNvDiagnosticCheckpoints && SupportsNvDiagnosticCheckpoints;
        if (!wantsCrashMarker && !wantsNvCheckpoint)
            return;

        ulong serial = unchecked((ulong)Interlocked.Increment(ref _frameTelemetry._vulkanCommandDiagnosticMarkerSerial));
        VulkanCommandDiagnosticMarker marker = new()
        {
            Serial = serial,
            OpKind = op.GetType().Name,
            OutputTargetName = ResolveFrameOpDiagnosticTargetName(op),
            PassIndex = passIndex,
            BatchIndex = batchIndex,
            PipelineIdentity = op.Context.PipelineIdentity,
            ViewportIdentity = op.Context.ViewportIdentity,
            CommandBufferHandle = unchecked((ulong)commandBuffer.Handle),
            CommandBufferRecordingGeneration = ResolveCommandBufferRecordingGeneration(commandBuffer),
        };

        lock (_frameTelemetry._vulkanSubmissionDiagnosticsLock)
        {
            int index = unchecked((int)((serial - 1UL) % VulkanCommandDiagnosticMarkerCapacity));
            _frameTelemetry._vulkanCommandDiagnosticMarkers[index] = marker;
        }
        if (_frameTelemetry._diagnosticOptions.EnableCommandBufferLabels)
        {
            SetDebugObjectName(
                ObjectType.CommandBuffer,
                marker.CommandBufferHandle,
                $"FrameOpContext.{marker.OpKind}.Pass{marker.PassIndex}.Pipe{marker.PipelineIdentity}.Vp{marker.ViewportIdentity}");
        }

        if (wantsNvCheckpoint)
            TrySetNvDiagnosticCheckpoint(commandBuffer, marker);
    }

    /// <summary>
    /// Resolves the diagnostic target name for the specified frame operation.
    /// </summary>
    /// <param name="op">The frame operation for which to resolve the diagnostic target name.</param>
    /// <returns>The resolved diagnostic target name.</returns>
    private static string ResolveFrameOpDiagnosticTargetName(FrameOp op)
    {
        if (!string.IsNullOrWhiteSpace(op.Context.OutputTargetName))
            return op.Context.OutputTargetName!;
        if (!string.IsNullOrWhiteSpace(op.Target?.Name))
            return op.Target!.Name!;
        return "<swapchain>";
    }

    /// <summary>
    /// Attempts to set an NVIDIA diagnostic checkpoint for the specified command buffer and diagnostic marker.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer for which to set the diagnostic checkpoint.</param>
    /// <param name="marker">The diagnostic marker containing information about the command.</param>
    private void TrySetNvDiagnosticCheckpoint(CommandBuffer commandBuffer, VulkanCommandDiagnosticMarker marker)
    {
        if (_deviceContext.ExtensionFunctions.NvDeviceDiagnosticCheckpoints is null || !SupportsNvDiagnosticCheckpoints || commandBuffer.Handle == 0)
            return;

        int index = unchecked((int)((marker.Serial - 1UL) % VulkanNvCheckpointMarkerCapacity));
        lock (_frameTelemetry._vulkanNvCheckpointMarkerLock)
        {
            _frameTelemetry._vulkanNvCheckpointMarkers[index] = new()
            {
                Serial = marker.Serial,
                OpKind = marker.OpKind,
                OutputTargetName = marker.OutputTargetName,
                PassIndex = marker.PassIndex,
                BatchIndex = marker.BatchIndex,
                PipelineIdentity = marker.PipelineIdentity,
                ViewportIdentity = marker.ViewportIdentity,
                CommandBufferHandle = marker.CommandBufferHandle,
                CommandBufferRecordingGeneration = marker.CommandBufferRecordingGeneration,
            };
            _deviceContext.ExtensionFunctions.NvDeviceDiagnosticCheckpoints.CmdSetCheckpoint(commandBuffer, (void*)(nuint)marker.Serial);
        }
    }

    /// <summary>
    /// Resolves the human-readable representation of an NVIDIA checkpoint marker given its pointer.
    /// </summary>
    /// <param name="markerPtr">A pointer to the NVIDIA checkpoint marker.</param>
    /// <returns>A string representing the resolved checkpoint marker, or an appropriate placeholder if the marker is null, zero, or evicted.</returns>
    private string ResolveNvCheckpointMarker(void* markerPtr)
    {
        if (markerPtr is null)
            return "<null>";

        ulong serial = (ulong)(nuint)markerPtr;
        if (serial == 0)
            return "<zero>";

        int index = unchecked((int)((serial - 1UL) % VulkanNvCheckpointMarkerCapacity));
        lock (_frameTelemetry._vulkanNvCheckpointMarkerLock)
        {
            VulkanNvCheckpointMarker marker = _frameTelemetry._vulkanNvCheckpointMarkers[index];
            return marker.Serial != serial
                ? $"#{serial}:<evicted>"
                : $"#{marker.Serial}:{marker.OpKind ?? "<unknown>"} " +
                    $"target={marker.OutputTargetName ?? "<unknown>"} " +
                    $"pass={marker.PassIndex} batch={marker.BatchIndex} " +
                    $"pipe={marker.PipelineIdentity} vp={marker.ViewportIdentity} " +
                    $"cmd=0x{marker.CommandBufferHandle:X} cmdRecordGen={marker.CommandBufferRecordingGeneration}";
        }
    }

    /// <summary>
    /// Registers a range of Vulkan device addresses for diagnostic purposes. This allows tracking of memory regions associated with specific Vulkan buffers.
    /// </summary>
    /// <param name="buffer">The Vulkan buffer associated with the device address range.</param>
    /// <param name="baseAddress">The base address of the device address range.</param>
    /// <param name="size">The size of the device address range.</param>
    /// <param name="label">A label describing the purpose or usage of the device address range.</param>
    private void RegisterVulkanDeviceAddressRange(Buffer buffer, ulong baseAddress, ulong size, string label)
    {
        if (buffer.Handle == 0 || baseAddress == 0 || size == 0)
            return;

        lock (_frameTelemetry._vulkanDeviceAddressDiagnosticsLock)
        {
            int firstInactive = -1;
            for (int i = 0; i < _frameTelemetry._vulkanDeviceAddressRanges.Length; i++)
            {
                VulkanDeviceAddressRange existing = _frameTelemetry._vulkanDeviceAddressRanges[i];
                if (existing.Active && existing.Buffer.Handle == buffer.Handle)
                {
                    _frameTelemetry._vulkanDeviceAddressRanges[i] = new(buffer, baseAddress, size, label, Active: true);
                    return;
                }

                if (!existing.Active && firstInactive < 0)
                    firstInactive = i;
            }

            int index = firstInactive >= 0
                ? firstInactive
                : unchecked((int)(buffer.Handle % (ulong)VulkanDeviceAddressRangeCapacity));
            _frameTelemetry._vulkanDeviceAddressRanges[index] = new(buffer, baseAddress, size, label, Active: true);
        }
    }

    /// <summary>
    /// Unregisters a range of Vulkan device addresses associated with the specified buffer, marking them as inactive for diagnostic purposes.
    /// </summary>
    /// <param name="buffer">The Vulkan buffer whose associated device address range should be unregistered.</param>
    private void UnregisterVulkanDeviceAddressRange(Buffer buffer)
    {
        if (buffer.Handle == 0)
            return;

        lock (_frameTelemetry._vulkanDeviceAddressDiagnosticsLock)
        {
            for (int i = 0; i < _frameTelemetry._vulkanDeviceAddressRanges.Length; i++)
            {
                VulkanDeviceAddressRange existing = _frameTelemetry._vulkanDeviceAddressRanges[i];
                if (existing.Active && existing.Buffer.Handle == buffer.Handle)
                    _frameTelemetry._vulkanDeviceAddressRanges[i] = existing with { Active = false };
            }
        }
    }

    private void ImportValidationDeviceAddressBindings()
    {
        if (!_frameTelemetry._diagnosticOptions.RequestDeviceAddressBindingReport ||
            !SupportsDeviceAddressBindingReport)
            return;

        VulkanValidationDeviceAddressBinding[] bindings =
            _deviceContext.ValidationDiagnostics.DrainDeviceAddressBindings(out int overflowCount);
        for (int i = 0; i < bindings.Length; i++)
        {
            VulkanValidationDeviceAddressBinding binding = bindings[i];
            string? correlatedObject = DescribeVulkanAddressCorrelation(binding.BaseAddress);
            long serial = Interlocked.Increment(ref _frameTelemetry._vulkanDeviceAddressBindingEventSerial);
            int index = unchecked((int)((serial - 1) % VulkanDeviceAddressBindingEventCapacity));
            lock (_frameTelemetry._vulkanDeviceAddressDiagnosticsLock)
            {
                _frameTelemetry._vulkanDeviceAddressBindingEvents[index] = new(
                    unchecked((ulong)serial),
                    binding.BaseAddress,
                    binding.Size,
                    binding.BindingType,
                    binding.Flags,
                    correlatedObject);
            }
        }

        if (overflowCount > 0)
            Debug.VulkanWarning($"[Vulkan] Dropped {overflowCount} device-address binding callbacks before device-loss reporting.");
    }

    /// <summary>
    /// Describes the correlation of a Vulkan device address with known device address ranges.
    /// </summary>
    /// <param name="address">The Vulkan device address to describe.</param>
    /// <returns>A string describing the correlation of the address with known device address ranges, or null if no correlation is found.</returns>
    private string? DescribeVulkanAddressCorrelation(ulong address)
    {
        if (address == 0)
            return null;

        lock (_frameTelemetry._vulkanDeviceAddressDiagnosticsLock)
        {
            for (int i = 0; i < _frameTelemetry._vulkanDeviceAddressRanges.Length; i++)
            {
                VulkanDeviceAddressRange range = _frameTelemetry._vulkanDeviceAddressRanges[i];
                if (!range.Active || range.BaseAddress == 0 || range.Size == 0)
                    continue;

                if (address >= range.BaseAddress && address - range.BaseAddress < range.Size)
                {
                    return
                        $"{range.Label ?? "Buffer"} " +
                        $"buffer=0x{range.Buffer.Handle:X} " +
                        $"range=0x{range.BaseAddress:X}+0x{range.Size:X}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Appends a summary of Vulkan device address binding events to the specified StringBuilder.
    /// </summary>
    /// <param name="builder">The StringBuilder to which the summary will be appended.</param>
    private void AppendDeviceAddressBindingSummary(StringBuilder builder)
    {
        if (!_frameTelemetry._diagnosticOptions.RequestDeviceAddressBindingReport)
            return;

        if (!SupportsDeviceAddressBindingReport)
        {
            AppendFaultSection(builder, "AddressBindingReport unavailable");
            return;
        }

        long latestSerial = Volatile.Read(ref _frameTelemetry._vulkanDeviceAddressBindingEventSerial);
        int activeRangeCount = CountTrackedVulkanDeviceAddressRanges();
        if (latestSerial <= 0)
        {
            AppendFaultSection(builder, $"AddressBindingReport events=0 activeRanges={activeRangeCount}");
            return;
        }

        StringBuilder section = new();
        section.Append("AddressBindingReport events=").Append(latestSerial).Append(" activeRanges=").Append(activeRangeCount);

        int emitted = 0;
        lock (_frameTelemetry._vulkanDeviceAddressDiagnosticsLock)
        {
            for (long serial = latestSerial; serial > 0 && emitted < MaxDeviceAddressBindingReportEntries; serial--)
            {
                int index = unchecked((int)((serial - 1) % VulkanDeviceAddressBindingEventCapacity));
                VulkanDeviceAddressBindingEvent evt = _frameTelemetry._vulkanDeviceAddressBindingEvents[index];
                if (evt.Serial != unchecked((ulong)serial))
                    continue;

                section
                    .Append(" [#").Append(evt.Serial)
                    .Append(' ').Append(evt.BindingType)
                    .Append(" flags=").Append(evt.Flags)
                    .Append(" range=0x").Append(evt.BaseAddress.ToString("X"))
                    .Append("+0x").Append(evt.Size.ToString("X"))
                    .Append(" object=").Append(evt.CorrelatedObject ?? "<untracked>")
                    .Append(']');
                emitted++;
            }
        }

        AppendFaultSection(builder, section.ToString());
    }

    /// <summary>
    /// Counts the number of currently active Vulkan device address ranges.
    /// </summary>
    /// <returns>The number of currently active Vulkan device address ranges.</returns>
    private int CountTrackedVulkanDeviceAddressRanges()
    {
        int count = 0;
        lock (_frameTelemetry._vulkanDeviceAddressDiagnosticsLock)
        {
            for (int i = 0; i < _frameTelemetry._vulkanDeviceAddressRanges.Length; i++)
                if (_frameTelemetry._vulkanDeviceAddressRanges[i].Active)
                    count++;
        }

        return count;
    }

    /// <summary>
    /// Appends a detailed summary of device faults to the specified StringBuilder.
    /// </summary>
    /// <param name="builder">The StringBuilder to which the detailed summary will be appended.</param>
    private void AppendDeviceFaultSummaryDetailed(StringBuilder builder)
    {
        if (!_frameTelemetry._diagnosticOptions.RequestDeviceFault)
            return;

        bool khrExposed = _deviceContext.AvailableDeviceExtensions.Contains(KhrDeviceFaultExtensionName);
        bool khrQueried = _deviceContext.TryAppendPersistedKhrDeviceFaultSummary(
            builder,
            _frameTelemetry._diagnosticOptions,
            includeVendorBinary: _deviceLost);
        if (!_deviceLost)
        {
            if (!khrQueried)
                AppendFaultSection(builder, $"DeviceFault querySkipped=not-device-lost khrExposed={khrExposed}");
            return;
        }

        VulkanDeviceFaultFacility deviceFaultFacility = _deviceContext.DeviceFaultFacility;
        if (deviceFaultFacility.IsUsingKhrDeviceFault && khrQueried)
            return;

        ExtDeviceFault? extDeviceFault = _deviceContext.ExtensionFunctions.ExtDeviceFault;
        if (extDeviceFault is null || !deviceFaultFacility.SupportsExtDeviceFault)
        {
            AppendFaultSection(
                builder,
                $"DeviceFaultEXT unavailable khrExposed={khrExposed} khrActive={deviceFaultFacility.IsUsingKhrDeviceFault} khrFunctionTable={deviceFaultFacility.GetDeviceFaultReportsKhr is not null}");
            return;
        }

        _deviceContext.TryAppendPersistedExtDeviceFaultSummary(
            builder,
            extDeviceFault,
            _frameTelemetry._diagnosticOptions,
            khrExposed,
            _deviceContext.Capabilities.Supports(EVulkanDeviceCapability.DeviceFaultVendorBinary));
    }

    /// <summary>
    /// Appends a detailed summary of NVIDIA diagnostic checkpoints to the device fault report.
    /// </summary>
    /// <param name="builder">The StringBuilder to which the NVIDIA checkpoint summary will be appended.</param>
    private void AppendNvCheckpointSummaryDetailed(StringBuilder builder)
    {
        if (!_frameTelemetry._diagnosticOptions.RequestNvDiagnosticCheckpoints)
            return;

        if (_deviceContext.ExtensionFunctions.NvDeviceDiagnosticCheckpoints is null || !SupportsNvDiagnosticCheckpoints)
        {
            AppendFaultSection(builder, "NvCheckpoints unavailable");
            return;
        }

        try
        {
            StringBuilder section = new("NvCheckpoints");
            AppendNvQueueCheckpointData(section, _deviceContext.GraphicsQueue, "graphics");
            if (_deviceContext.PresentQueue.Handle != _deviceContext.GraphicsQueue.Handle)
                AppendNvQueueCheckpointData(section, _deviceContext.PresentQueue, "present");
            if (_deviceContext.TransferQueue.Handle != 0 && _deviceContext.TransferQueue.Handle != _deviceContext.GraphicsQueue.Handle && _deviceContext.TransferQueue.Handle != _deviceContext.PresentQueue.Handle)
                AppendNvQueueCheckpointData(section, _deviceContext.TransferQueue, "transfer");
            AppendFaultSection(builder, section.ToString());
        }
        catch (Exception ex)
        {
            AppendFaultSection(builder, $"NvCheckpoints queryFailed={ex.GetType().Name}:{ex.Message}");
        }
    }

    /// <summary>
    /// Appends detailed NVIDIA checkpoint data for a specific Vulkan queue to the given section of the device fault report.
    /// </summary>
    /// <param name="section">The StringBuilder section to which the checkpoint data will be appended.</param>
    /// <param name="queue">The Vulkan queue for which checkpoint data will be retrieved.</param>
    /// <param name="queueName">The name of the Vulkan queue (e.g., "graphics", "present", "transfer").</param>
    private void AppendNvQueueCheckpointData(StringBuilder section, Queue queue, string queueName)
    {
        if (queue.Handle == 0 || _deviceContext.ExtensionFunctions.NvDeviceDiagnosticCheckpoints is null)
            return;

        uint count = 0;
        _deviceContext.ExtensionFunctions.NvDeviceDiagnosticCheckpoints.GetQueueCheckpointData2(queue, &count, null);
        section.Append(' ').Append(queueName).Append('=').Append(count);
        if (count == 0)
            return;

        CheckpointData2NV[] checkpoints = new CheckpointData2NV[checked((int)count)];
        for (int i = 0; i < checkpoints.Length; i++)
        {
            checkpoints[i] = new()
            {
                SType = StructureType.CheckpointData2NV,
                PNext = null,
            };
        }

        fixed (CheckpointData2NV* checkpointPtr = checkpoints)
        {
            uint writableCount = count;
            _deviceContext.ExtensionFunctions.NvDeviceDiagnosticCheckpoints.GetQueueCheckpointData2(queue, &writableCount, checkpointPtr);
            int emitted = Math.Min((int)writableCount, MaxNvCheckpointReportEntries);
            for (int i = 0; i < emitted; i++)
            {
                section
                    .Append(" [").Append(queueName).Append('#').Append(i)
                    .Append(" stage=").Append(checkpoints[i].Stage)
                    .Append(" marker=").Append(ResolveNvCheckpointMarker(checkpoints[i].PCheckpointMarker))
                    .Append(']');
            }
        }
    }

    /// <summary>
    /// Reads a null-terminated UTF-8 string from the specified byte pointer, up to a maximum number of bytes.
    /// </summary>
    /// <param name="bytes">A pointer to the byte array containing the UTF-8 string.</param>
    /// <param name="maxBytes">The maximum number of bytes to read from the byte array.</param>
    /// <returns>The decoded string, or an empty string if the byte pointer is null or the maximum number of bytes is zero.</returns>
    private static string ReadNullTerminatedUtf8(byte* bytes, int maxBytes)
    {
        if (bytes is null || maxBytes <= 0)
            return string.Empty;

        int length = 0;
        while (length < maxBytes && bytes[length] != 0)
            length++;

        return length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(bytes, length));
    }

    /// <summary>
    /// Summarizes a string for inline logging by truncating it to a specified maximum length and replacing line breaks with spaces.
    /// </summary>
    /// <param name="value">The string value to summarize for inline logging.</param>
    /// <param name="maxLength">The maximum length of the summarized string. Defaults to 96 characters.</param>
    /// <returns>A summarized version of the input string suitable for inline logging.</returns>
    private static string SummarizeForInlineLog(string value, int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";

        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "...";
    }
}

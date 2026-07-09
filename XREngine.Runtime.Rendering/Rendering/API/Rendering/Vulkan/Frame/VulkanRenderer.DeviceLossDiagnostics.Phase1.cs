using System;
using System.IO;
using System.Text;
using Silk.NET.Vulkan;
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
        => ReleaseKhrDeviceFaultFunctionPointers();

    /// <summary>
    /// Takes a snapshot of the Vulkan command diagnostic marker for the given submit info.
    /// </summary>
    /// <param name="submitInfo">The Vulkan submit info containing the command buffers to snapshot.</param>
    /// <returns>The Vulkan command diagnostic marker corresponding to the given submit info, or the default marker if none is found.</returns>
    private VulkanCommandDiagnosticMarker SnapshotVulkanCommandDiagnosticMarker(ref SubmitInfo submitInfo)
    {
        if (submitInfo.CommandBufferCount == 0 || submitInfo.PCommandBuffers is null)
            return default;

        lock (_vulkanSubmissionDiagnosticsLock)
        {
            long latestSerial = Volatile.Read(ref _vulkanCommandDiagnosticMarkerSerial);
            int available = (int)Math.Min(latestSerial, VulkanCommandDiagnosticMarkerCapacity);
            for (long serial = latestSerial; serial > 0 && latestSerial - serial < available; serial--)
            {
                int index = unchecked((int)((serial - 1) % VulkanCommandDiagnosticMarkerCapacity));
                VulkanCommandDiagnosticMarker marker = _vulkanCommandDiagnosticMarkers[index];
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

        Interlocked.CompareExchange(ref _firstFailingVulkanApi, api, null);
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
        if (!_diagnosticOptions.EnableCrashBreadcrumbs || imageBarrierCount == 0 || imageBarriers is null)
            return;

        lock (_vulkanSubmissionDiagnosticsLock)
        {
            for (uint i = 0; i < imageBarrierCount; i++)
            {
                ImageMemoryBarrier barrier = imageBarriers[i];
                long serial = Interlocked.Increment(ref _vulkanImageLayoutTransitionSerial);
                int index = unchecked((int)((serial - 1) % VulkanImageLayoutTransitionCapacity));
                _vulkanImageLayoutTransitions[index] = new(
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
    private void RecordVulkanDescriptorTableGeneration(string reason)
    {
        if (!_diagnosticOptions.EnableCrashBreadcrumbs)
            return;

        Interlocked.Increment(ref _vulkanDescriptorTableGeneration);
    }

    /// <summary>
    /// Records a Vulkan command diagnostic marker for the specified command buffer and frame operation.
    /// </summary>
    /// <param name="commandBuffer">The Vulkan command buffer associated with the command.</param>
    /// <param name="op">The frame operation being executed.</param>
    /// <param name="passIndex">The index of the pass within the frame operation.</param>
    /// <param name="batchIndex">The index of the batch within the pass.</param>
    private void RecordVulkanCommandDiagnosticMarker(CommandBuffer commandBuffer, FrameOp op, int passIndex, int batchIndex)
    {
        bool wantsCrashMarker = _diagnosticOptions.EnableCrashBreadcrumbs;
        bool wantsNvCheckpoint = _diagnosticOptions.RequestNvDiagnosticCheckpoints && SupportsNvDiagnosticCheckpoints;
        if (!wantsCrashMarker && !wantsNvCheckpoint)
            return;

        ulong serial = unchecked((ulong)Interlocked.Increment(ref _vulkanCommandDiagnosticMarkerSerial));
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

        lock (_vulkanSubmissionDiagnosticsLock)
        {
            int index = unchecked((int)((serial - 1UL) % VulkanCommandDiagnosticMarkerCapacity));
            _vulkanCommandDiagnosticMarkers[index] = marker;
        }
        if (_diagnosticOptions.EnableCommandBufferLabels)
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
        if (_nvDeviceDiagnosticCheckpoints is null || !_supportsNvDiagnosticCheckpoints || commandBuffer.Handle == 0)
            return;

        int index = unchecked((int)((marker.Serial - 1UL) % VulkanNvCheckpointMarkerCapacity));
        lock (_vulkanNvCheckpointMarkerLock)
        {
            _vulkanNvCheckpointMarkers[index] = new()
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
            _nvDeviceDiagnosticCheckpoints.CmdSetCheckpoint(commandBuffer, (void*)(nuint)marker.Serial);
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
        lock (_vulkanNvCheckpointMarkerLock)
        {
            VulkanNvCheckpointMarker marker = _vulkanNvCheckpointMarkers[index];
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

        lock (_vulkanDeviceAddressDiagnosticsLock)
        {
            int firstInactive = -1;
            for (int i = 0; i < _vulkanDeviceAddressRanges.Length; i++)
            {
                VulkanDeviceAddressRange existing = _vulkanDeviceAddressRanges[i];
                if (existing.Active && existing.Buffer.Handle == buffer.Handle)
                {
                    _vulkanDeviceAddressRanges[i] = new(buffer, baseAddress, size, label, Active: true);
                    return;
                }

                if (!existing.Active && firstInactive < 0)
                    firstInactive = i;
            }

            int index = firstInactive >= 0
                ? firstInactive
                : unchecked((int)(buffer.Handle % (ulong)VulkanDeviceAddressRangeCapacity));
            _vulkanDeviceAddressRanges[index] = new(buffer, baseAddress, size, label, Active: true);
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

        lock (_vulkanDeviceAddressDiagnosticsLock)
        {
            for (int i = 0; i < _vulkanDeviceAddressRanges.Length; i++)
            {
                VulkanDeviceAddressRange existing = _vulkanDeviceAddressRanges[i];
                if (existing.Active && existing.Buffer.Handle == buffer.Handle)
                    _vulkanDeviceAddressRanges[i] = existing with { Active = false };
            }
        }
    }

    /// <summary>
    /// Records a Vulkan device address binding callback for diagnostic purposes.
    /// </summary>
    /// <param name="callbackData">The callback data containing information about the Vulkan device address binding event.</param>
    private void RecordVulkanDeviceAddressBindingCallback(DebugUtilsMessengerCallbackDataEXT* callbackData)
    {
        if (!_diagnosticOptions.RequestDeviceAddressBindingReport ||
            !_supportsDeviceAddressBindingReport ||
            callbackData is null)
            return;

        BaseInStructure* current = (BaseInStructure*)callbackData->PNext;
        while (current is not null)
        {
            if (current->SType == StructureType.DeviceAddressBindingCallbackDataExt)
            {
                DeviceAddressBindingCallbackDataEXT* binding = (DeviceAddressBindingCallbackDataEXT*)current;
                RecordVulkanDeviceAddressBindingEvent(binding);
            }

            current = current->PNext;
        }
    }

    /// <summary>
    /// Records a Vulkan device address binding event for diagnostic purposes.
    /// </summary>
    /// <param name="binding">The binding data containing information about the Vulkan device address binding event.</param>
    private void RecordVulkanDeviceAddressBindingEvent(DeviceAddressBindingCallbackDataEXT* binding)
    {
        if (binding is null || binding->BaseAddress == 0 || binding->Size == 0)
            return;

        string? correlatedObject = DescribeVulkanAddressCorrelation(binding->BaseAddress);
        long serial = Interlocked.Increment(ref _vulkanDeviceAddressBindingEventSerial);
        int index = unchecked((int)((serial - 1) % VulkanDeviceAddressBindingEventCapacity));
        lock (_vulkanDeviceAddressDiagnosticsLock)
        {
            _vulkanDeviceAddressBindingEvents[index] = new(
                unchecked((ulong)serial),
                binding->BaseAddress,
                binding->Size,
                binding->BindingType,
                binding->Flags,
                correlatedObject);
        }
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

        lock (_vulkanDeviceAddressDiagnosticsLock)
        {
            for (int i = 0; i < _vulkanDeviceAddressRanges.Length; i++)
            {
                VulkanDeviceAddressRange range = _vulkanDeviceAddressRanges[i];
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
        if (!_diagnosticOptions.RequestDeviceAddressBindingReport)
            return;

        if (!_supportsDeviceAddressBindingReport)
        {
            AppendFaultSection(builder, "AddressBindingReport unavailable");
            return;
        }

        long latestSerial = Volatile.Read(ref _vulkanDeviceAddressBindingEventSerial);
        int activeRangeCount = CountTrackedVulkanDeviceAddressRanges();
        if (latestSerial <= 0)
        {
            AppendFaultSection(builder, $"AddressBindingReport events=0 activeRanges={activeRangeCount}");
            return;
        }

        StringBuilder section = new();
        section.Append("AddressBindingReport events=").Append(latestSerial).Append(" activeRanges=").Append(activeRangeCount);

        int emitted = 0;
        lock (_vulkanDeviceAddressDiagnosticsLock)
        {
            for (long serial = latestSerial; serial > 0 && emitted < MaxDeviceAddressBindingReportEntries; serial--)
            {
                int index = unchecked((int)((serial - 1) % VulkanDeviceAddressBindingEventCapacity));
                VulkanDeviceAddressBindingEvent evt = _vulkanDeviceAddressBindingEvents[index];
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
        lock (_vulkanDeviceAddressDiagnosticsLock)
        {
            for (int i = 0; i < _vulkanDeviceAddressRanges.Length; i++)
                if (_vulkanDeviceAddressRanges[i].Active)
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
        if (!_diagnosticOptions.RequestDeviceFault)
            return;

        bool khrExposed = Array.Exists(_availableDeviceExtensions, static x => x == KhrDeviceFaultExtensionName);
        bool khrQueried = TryAppendKhrDeviceFaultSummary(builder);
        if (!_deviceLost)
        {
            if (!khrQueried)
                AppendFaultSection(builder, $"DeviceFault querySkipped=not-device-lost khrExposed={khrExposed}");
            return;
        }

        if (_deviceFaultUsingKhr && khrQueried)
            return;

        if (_extDeviceFault is null || !_supportsExtDeviceFault)
        {
            AppendFaultSection(
                builder,
                $"DeviceFaultEXT unavailable khrExposed={khrExposed} khrActive={_deviceFaultUsingKhr} khrFunctionTable={_vkGetDeviceFaultReportsKHR is not null}");
            return;
        }

        try
        {
            DeviceFaultCountsEXT counts = new()
            {
                SType = StructureType.DeviceFaultCountsExt,
                PNext = null,
            };

            Result countsResult = _extDeviceFault.GetDeviceFaultInfo(device, &counts, null);
            if (countsResult is not (Result.Success or Result.Incomplete))
            {
                AppendFaultSection(builder, $"DeviceFaultEXT countsResult={countsResult} khrExposed={khrExposed}");
                return;
            }

            uint reportedAddressInfoCount = counts.AddressInfoCount;
            uint reportedVendorInfoCount = counts.VendorInfoCount;
            ulong reportedVendorBinarySize = counts.VendorBinarySize;
            uint writableAddressInfoCount = Math.Min(reportedAddressInfoCount, (uint)_diagnosticOptions.DeviceFaultAddressRecordCap);
            uint writableVendorInfoCount = Math.Min(reportedVendorInfoCount, (uint)_diagnosticOptions.DeviceFaultVendorRecordCap);
            ulong writableVendorBinarySize = Math.Min(reportedVendorBinarySize, (ulong)_diagnosticOptions.DeviceFaultVendorBinaryByteCap);
            bool recordsTruncated = writableAddressInfoCount < reportedAddressInfoCount ||
                writableVendorInfoCount < reportedVendorInfoCount;

            DeviceFaultAddressInfoEXT[] addressInfos = writableAddressInfoCount == 0
                ? Array.Empty<DeviceFaultAddressInfoEXT>()
                : new DeviceFaultAddressInfoEXT[checked((int)writableAddressInfoCount)];
            DeviceFaultVendorInfoEXT[] vendorInfos = writableVendorInfoCount == 0
                ? Array.Empty<DeviceFaultVendorInfoEXT>()
                : new DeviceFaultVendorInfoEXT[checked((int)writableVendorInfoCount)];

            ulong vendorBinarySize = writableVendorBinarySize;
            byte[]? vendorBinary = null;
            string vendorBinaryStatus;
            if (!_supportsExtDeviceFaultVendorBinary)
            {
                vendorBinaryStatus = "feature-disabled";
            }
            else if (vendorBinarySize == 0)
            {
                vendorBinaryStatus = "not-reported";
            }
            else
            {
                vendorBinary = new byte[(int)vendorBinarySize];
                vendorBinaryStatus = vendorBinarySize < reportedVendorBinarySize
                    ? $"captured-truncated:{vendorBinarySize}/{reportedVendorBinarySize}"
                    : "captured";
            }

            counts.AddressInfoCount = writableAddressInfoCount;
            counts.VendorInfoCount = writableVendorInfoCount;
            counts.VendorBinarySize = vendorBinary?.LongLength is > 0 ? vendorBinarySize : 0UL;

            DeviceFaultInfoEXT faultInfo = new()
            {
                SType = StructureType.DeviceFaultInfoExt,
                PNext = null,
            };

            byte[] vendorBinaryBuffer = vendorBinary ?? Array.Empty<byte>();
            fixed (DeviceFaultAddressInfoEXT* addressInfosPtr = addressInfos)
            fixed (DeviceFaultVendorInfoEXT* vendorInfosPtr = vendorInfos)
            fixed (byte* vendorBinaryPtr = vendorBinaryBuffer)
            {
                faultInfo.PAddressInfos = addressInfos.Length == 0 ? null : addressInfosPtr;
                faultInfo.PVendorInfos = vendorInfos.Length == 0 ? null : vendorInfosPtr;
                faultInfo.PVendorBinaryData = vendorBinaryBuffer.Length == 0 ? null : vendorBinaryPtr;

                Result infoResult = _extDeviceFault.GetDeviceFaultInfo(device, &counts, &faultInfo);
                bool infoResultUsable = infoResult is Result.Success or Result.Incomplete;
                if (!infoResultUsable)
                {
                    vendorBinary = null;
                    vendorBinaryStatus = $"failed-unusable:{infoResult}";
                }
                bool incomplete = countsResult == Result.Incomplete ||
                    infoResult != Result.Success ||
                    recordsTruncated ||
                    vendorBinarySize < reportedVendorBinarySize;
                string description = ReadNullTerminatedUtf8(faultInfo.Description, VulkanDeviceFaultDescriptionBytes);

                string artifactSummary = PersistDeviceFaultArtifacts(
                    description,
                    counts,
                    addressInfos,
                    vendorInfos,
                    vendorBinary,
                    countsResult,
                    infoResult,
                    incomplete,
                    vendorBinaryStatus,
                    khrExposed);

                AppendFaultSection(
                    builder,
                    $"DeviceFaultEXT countsResult={countsResult} infoResult={infoResult} incomplete={incomplete} " +
                    $"addressInfos={counts.AddressInfoCount}/{reportedAddressInfoCount} vendorInfos={counts.VendorInfoCount}/{reportedVendorInfoCount} " +
                    $"vendorBinaryBytes={vendorBinarySize}/{reportedVendorBinarySize} vendorBinary={vendorBinaryStatus} " +
                    $"description='{SummarizeForInlineLog(description)}' {artifactSummary}");
            }
        }
        catch (Exception ex)
        {
            AppendFaultSection(builder, $"DeviceFaultEXT queryFailed={ex.GetType().Name}:{ex.Message}");
        }
    }

    /// <summary>
    /// Persists the artifacts related to a Vulkan device fault and generates a summary string.
    /// </summary>
    /// <param name="description">The description of the device fault.</param>
    /// <param name="counts">The counts of various device fault artifacts.</param>
    /// <param name="addressInfos">The array of address information related to the device fault.</param>
    /// <param name="vendorInfos">The array of vendor-specific information related to the device fault.</param>
    /// <param name="vendorBinary">The vendor binary data associated with the device fault, if any.</param>
    /// <param name="countsResult">The result of querying the counts of device fault artifacts.</param>
    /// <param name="infoResult">The result of querying the detailed device fault information.</param>
    /// <param name="incomplete">Indicates whether the device fault information is incomplete.</param>
    /// <param name="vendorBinaryStatus">The status of the vendor binary data.</param>
    /// <param name="khrExposed">Indicates whether the KHR device fault extension is exposed.</param>
    /// <returns>A summary string describing the persisted device fault artifacts.</returns>
    private string PersistDeviceFaultArtifacts(
        string description,
        in DeviceFaultCountsEXT counts,
        DeviceFaultAddressInfoEXT[] addressInfos,
        DeviceFaultVendorInfoEXT[] vendorInfos,
        byte[]? vendorBinary,
        Result countsResult,
        Result infoResult,
        bool incomplete,
        string vendorBinaryStatus,
        bool khrExposed)
    {
        try
        {
            StringBuilder report = new();
            report.AppendLine("Vulkan Device Fault Report");
            report.Append("Utc=").AppendLine(DateTimeOffset.UtcNow.ToString("O"));
            report.Append("KHR device fault exposed=").Append(khrExposed)
                .Append(" active=").Append(_deviceFaultUsingKhr)
                .Append(" functionTable=").Append(_vkGetDeviceFaultReportsKHR is not null)
                .AppendLine();
            report.Append("CountsResult=").Append(countsResult).Append(" InfoResult=").Append(infoResult).Append(" Incomplete=").AppendLine(incomplete.ToString());
            report.Append("Description=").AppendLine(string.IsNullOrWhiteSpace(description) ? "<empty>" : description);
            report.Append("AddressInfoCount=").Append(counts.AddressInfoCount)
                .Append(" VendorInfoCount=").Append(counts.VendorInfoCount)
                .Append(" VendorBinarySize=").Append(counts.VendorBinarySize)
                .Append(" VendorBinaryStatus=").AppendLine(vendorBinaryStatus);

            int addressCount = Math.Min(addressInfos.Length, MaxDeviceFaultReportEntries);
            for (int i = 0; i < addressCount; i++)
            {
                DeviceFaultAddressInfoEXT info = addressInfos[i];
                report
                    .Append("Address[").Append(i).Append("] type=").Append(info.AddressType)
                    .Append(" reported=0x").Append(info.ReportedAddress.ToString("X"))
                    .Append(" precision=0x").Append(info.AddressPrecision.ToString("X"))
                    .Append(" object=").Append(DescribeVulkanAddressCorrelation(info.ReportedAddress) ?? "<untracked>")
                    .AppendLine();
            }

            int vendorCount = Math.Min(vendorInfos.Length, MaxDeviceFaultReportEntries);
            for (int i = 0; i < vendorCount; i++)
            {
                DeviceFaultVendorInfoEXT info = vendorInfos[i];
                string vendorDescription = ReadNullTerminatedUtf8(info.Description, VulkanDeviceFaultDescriptionBytes);

                report
                    .Append("Vendor[").Append(i).Append("] code=0x").Append(info.VendorFaultCode.ToString("X"))
                    .Append(" data=0x").Append(info.VendorFaultData.ToString("X"))
                    .Append(" description=").AppendLine(string.IsNullOrWhiteSpace(vendorDescription) ? "<empty>" : vendorDescription);
            }

            if (vendorBinary is { Length: > 0 })
                AppendVendorBinaryHeader(report, vendorBinary);

            const string reportFileName = "vulkan-device-fault-report.log";
            Debug.WriteAuxiliaryLog(reportFileName, report.ToString());

            string binarySummary = "vendorBinaryFile=<none>";
            if (vendorBinary is { Length: > 0 })
            {
                string directory = Debug.EnsureLogRunDirectory();
                string binaryFileName = $"vulkan-device-fault-vendor-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.bin";
                string binaryPath = Path.Combine(directory, binaryFileName);
                File.WriteAllBytes(binaryPath, vendorBinary);
                binarySummary = $"vendorBinaryFile={binaryFileName}";
            }

            return $"artifact={reportFileName} {binarySummary}";
        }
        catch (Exception ex)
        {
            return $"artifactWriteFailed={ex.GetType().Name}:{ex.Message}";
        }
    }

    /// <summary>
    /// Appends the header information of the vendor binary to the device fault report.
    /// </summary>
    /// <param name="report">The StringBuilder to which the vendor binary header information will be appended.</param>
    /// <param name="vendorBinary">The vendor binary data containing the header information.</param>
    private static void AppendVendorBinaryHeader(StringBuilder report, byte[] vendorBinary)
    {
        if (vendorBinary.Length < sizeof(DeviceFaultVendorBinaryHeaderVersionOneEXT))
            return;

        fixed (byte* binaryPtr = vendorBinary)
        {
            DeviceFaultVendorBinaryHeaderVersionOneEXT* header = (DeviceFaultVendorBinaryHeaderVersionOneEXT*)binaryPtr;
            report
                .Append("VendorBinaryHeader headerSize=").Append(header->HeaderSize)
                .Append(" version=").Append(header->HeaderVersion)
                .Append(" vendor=0x").Append(header->VendorID.ToString("X"))
                .Append(" device=0x").Append(header->DeviceID.ToString("X"))
                .Append(" driver=0x").Append(header->DriverVersion.ToString("X"))
                .Append(" api=0x").Append(header->ApiVersion.ToString("X"))
                .AppendLine();
        }
    }

    /// <summary>
    /// Appends a detailed summary of NVIDIA diagnostic checkpoints to the device fault report.
    /// </summary>
    /// <param name="builder">The StringBuilder to which the NVIDIA checkpoint summary will be appended.</param>
    private void AppendNvCheckpointSummaryDetailed(StringBuilder builder)
    {
        if (!_diagnosticOptions.RequestNvDiagnosticCheckpoints)
            return;

        if (_nvDeviceDiagnosticCheckpoints is null || !_supportsNvDiagnosticCheckpoints)
        {
            AppendFaultSection(builder, "NvCheckpoints unavailable");
            return;
        }

        try
        {
            StringBuilder section = new("NvCheckpoints");
            AppendNvQueueCheckpointData(section, graphicsQueue, "graphics");
            if (presentQueue.Handle != graphicsQueue.Handle)
                AppendNvQueueCheckpointData(section, presentQueue, "present");
            if (transferQueue.Handle != 0 && transferQueue.Handle != graphicsQueue.Handle && transferQueue.Handle != presentQueue.Handle)
                AppendNvQueueCheckpointData(section, transferQueue, "transfer");
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
        if (queue.Handle == 0 || _nvDeviceDiagnosticCheckpoints is null)
            return;

        uint count = 0;
        _nvDeviceDiagnosticCheckpoints.GetQueueCheckpointData2(queue, &count, null);
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
            _nvDeviceDiagnosticCheckpoints.GetQueueCheckpointData2(queue, &writableCount, checkpointPtr);
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

using System;
using System.IO;
using System.Runtime.InteropServices;
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

    private void ReleaseVulkanDiagnosticStorage()
    {
        ReleaseKhrDeviceFaultFunctionPointers();

        lock (_vulkanNvCheckpointMarkerLock)
        {
            if (_vulkanNvCheckpointMarkerPayloadsPin.IsAllocated)
                _vulkanNvCheckpointMarkerPayloadsPin.Free();
        }
    }

    private VulkanCommandDiagnosticMarker SnapshotLastVulkanCommandDiagnosticMarker()
        => _lastVulkanCommandDiagnosticMarker;

    private void RecordFirstFailingVulkanApi(string? api)
    {
        if (string.IsNullOrWhiteSpace(api))
            return;

        Interlocked.CompareExchange(ref _firstFailingVulkanApi, api, null);
    }

    private void RecordVulkanImageLayoutTransitionBreadcrumb(uint imageBarrierCount, ImageMemoryBarrier* imageBarriers, string? caller)
    {
        if (!_diagnosticOptions.EnableCrashBreadcrumbs || imageBarrierCount == 0 || imageBarriers is null)
            return;

        Interlocked.Add(ref _vulkanImageLayoutTransitionSerial, imageBarrierCount);
    }

    private void RecordVulkanDescriptorTableGeneration(string reason)
    {
        if (!_diagnosticOptions.EnableCrashBreadcrumbs)
            return;

        Interlocked.Increment(ref _vulkanDescriptorTableGeneration);
    }

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
        };

        _lastVulkanCommandDiagnosticMarker = marker;
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

    private static string ResolveFrameOpDiagnosticTargetName(FrameOp op)
    {
        if (!string.IsNullOrWhiteSpace(op.Context.OutputTargetName))
            return op.Context.OutputTargetName!;
        if (!string.IsNullOrWhiteSpace(op.Target?.Name))
            return op.Target!.Name!;
        return "<swapchain>";
    }

    private void TrySetNvDiagnosticCheckpoint(CommandBuffer commandBuffer, VulkanCommandDiagnosticMarker marker)
    {
        if (_nvDeviceDiagnosticCheckpoints is null || !_supportsNvDiagnosticCheckpoints || commandBuffer.Handle == 0)
            return;

        int index = unchecked((int)((marker.Serial - 1UL) % VulkanNvCheckpointMarkerCapacity));
        lock (_vulkanNvCheckpointMarkerLock)
        {
            if (!_vulkanNvCheckpointMarkerPayloadsPin.IsAllocated)
                _vulkanNvCheckpointMarkerPayloadsPin = GCHandle.Alloc(_vulkanNvCheckpointMarkerPayloads, GCHandleType.Pinned);

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
            };
            _vulkanNvCheckpointMarkerPayloads[index] = marker.Serial;

            byte* basePtr = (byte*)_vulkanNvCheckpointMarkerPayloadsPin.AddrOfPinnedObject();
            _nvDeviceDiagnosticCheckpoints.CmdSetCheckpoint(commandBuffer, basePtr + (index * sizeof(ulong)));
        }
    }

    private string ResolveNvCheckpointMarker(void* markerPtr)
    {
        if (markerPtr is null)
            return "<null>";

        ulong serial = *(ulong*)markerPtr;
        if (serial == 0)
            return "<zero>";

        int index = unchecked((int)((serial - 1UL) % VulkanNvCheckpointMarkerCapacity));
        lock (_vulkanNvCheckpointMarkerLock)
        {
            VulkanNvCheckpointMarker marker = _vulkanNvCheckpointMarkers[index];
            if (marker.Serial != serial)
                return $"#{serial}:<evicted>";

            return
                $"#{marker.Serial}:{marker.OpKind ?? "<unknown>"} " +
                $"target={marker.OutputTargetName ?? "<unknown>"} " +
                $"pass={marker.PassIndex} batch={marker.BatchIndex} " +
                $"pipe={marker.PipelineIdentity} vp={marker.ViewportIdentity} " +
                $"cmd=0x{marker.CommandBufferHandle:X}";
        }
    }

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

    private void RecordVulkanDeviceAddressBindingCallback(DebugUtilsMessengerCallbackDataEXT* callbackData)
    {
        if (!_diagnosticOptions.RequestDeviceAddressBindingReport ||
            !_supportsDeviceAddressBindingReport ||
            callbackData is null)
        {
            return;
        }

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

    private int CountTrackedVulkanDeviceAddressRanges()
    {
        int count = 0;
        lock (_vulkanDeviceAddressDiagnosticsLock)
        {
            for (int i = 0; i < _vulkanDeviceAddressRanges.Length; i++)
            {
                if (_vulkanDeviceAddressRanges[i].Active)
                    count++;
            }
        }

        return count;
    }

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

            DeviceFaultAddressInfoEXT[] addressInfos = counts.AddressInfoCount == 0
                ? Array.Empty<DeviceFaultAddressInfoEXT>()
                : new DeviceFaultAddressInfoEXT[checked((int)counts.AddressInfoCount)];
            DeviceFaultVendorInfoEXT[] vendorInfos = counts.VendorInfoCount == 0
                ? Array.Empty<DeviceFaultVendorInfoEXT>()
                : new DeviceFaultVendorInfoEXT[checked((int)counts.VendorInfoCount)];

            ulong vendorBinarySize = counts.VendorBinarySize;
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
            else if (vendorBinarySize > int.MaxValue)
            {
                vendorBinaryStatus = $"too-large:{vendorBinarySize}";
            }
            else
            {
                vendorBinary = new byte[(int)vendorBinarySize];
                vendorBinaryStatus = "captured";
            }

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
                bool incomplete = countsResult == Result.Incomplete || infoResult == Result.Incomplete;
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
                    $"addressInfos={counts.AddressInfoCount} vendorInfos={counts.VendorInfoCount} " +
                    $"vendorBinaryBytes={vendorBinarySize} vendorBinary={vendorBinaryStatus} " +
                    $"description='{SummarizeForInlineLog(description)}' {artifactSummary}");
            }
        }
        catch (Exception ex)
        {
            AppendFaultSection(builder, $"DeviceFaultEXT queryFailed={ex.GetType().Name}:{ex.Message}");
        }
    }

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

using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanDeviceFaultFacility
{
    private const uint PhysicalDeviceFeaturesSType = 1000573000;
    private const uint PhysicalDevicePropertiesSType = 1000573001;
    private const uint FaultInfoSType = 1000573002;
    private const uint FaultDebugInfoSType = 1000573003;
    private const int ReportBatchSize = 16;
    private const Result ErrorNotEnoughSpaceKhr = (Result)(-1000483000);

    /// <summary>Queries KHR capability structures without exposing native bootstrap state to the renderer.</summary>
    internal unsafe VulkanKhrDeviceFaultCapabilityQuery QueryKhrCapabilities(
        Vk api,
        PhysicalDevice physicalDevice,
        bool extensionEnabled)
    {
        if (!extensionEnabled)
            return default;

        VulkanKhrPhysicalDeviceFaultFeatures features = new()
        {
            SType = (StructureType)PhysicalDeviceFeaturesSType,
        };
        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };
        api.GetPhysicalDeviceFeatures2(physicalDevice, &features2);

        VulkanKhrPhysicalDeviceFaultProperties properties = new()
        {
            SType = (StructureType)PhysicalDevicePropertiesSType,
        };
        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &properties,
        };
        api.GetPhysicalDeviceProperties2(physicalDevice, &properties2);

        return new VulkanKhrDeviceFaultCapabilityQuery(
            features.DeviceFault != 0,
            features.DeviceFaultVendorBinary != 0,
            features.DeviceFaultReportMasked != 0,
            features.DeviceFaultDeviceLostOnMasked != 0,
            properties.MaxDeviceFaultCount);
    }

    /// <summary>Loads the complete KHR device-fault command table for the owned logical device.</summary>
    internal bool TryLoadKhrCommandTable(Vk api, Device device, out nint reportsAddress, out nint debugInfoAddress)
    {
        ResetKhrCommandTable();
        reportsAddress = 0;
        debugInfoAddress = 0;
        if (!SupportsKhrDeviceFault || device.Handle == 0)
            return false;

        reportsAddress = (nint)api.GetDeviceProcAddr(device, "vkGetDeviceFaultReportsKHR");
        debugInfoAddress = (nint)api.GetDeviceProcAddr(device, "vkGetDeviceFaultDebugInfoKHR");
        if (reportsAddress == 0 || debugInfoAddress == 0)
            return false;

        PublishKhrCommandTable(
            Marshal.GetDelegateForFunctionPointer<VkGetDeviceFaultReportsKhrDelegate>(reportsAddress),
            Marshal.GetDelegateForFunctionPointer<VkGetDeviceFaultDebugInfoKhrDelegate>(debugInfoAddress));
        return true;
    }

    /// <summary>
    /// Performs bounded KHR report retrieval and returns persistence-ready artifacts.
    /// This method is cold-path-only and never retains a renderer or callback.
    /// </summary>
    internal unsafe VulkanDeviceFaultCapture? CaptureKhr(
        Device device,
        in VulkanDiagnosticOptions options,
        bool includeVendorBinary)
    {
        if (!SupportsKhrDeviceFault)
            return null;
        if (GetDeviceFaultReportsKhr is null)
            return new VulkanDeviceFaultCapture("KHR advertised but function pointer unavailable");

        try
        {
            uint availableCount = 0;
            Result countResult = GetDeviceFaultReportsKhr(device, 0, &availableCount, null);
            if (countResult == Result.Timeout || availableCount == 0)
            {
                VulkanDeviceFaultCapture? debugCapture = includeVendorBinary
                    ? CaptureKhrDebugInfo(device, options)
                    : null;
                string emptySummary =
                    $"DeviceFaultKHR active reports=0 countResult={countResult} maxReports={KhrDeviceFaultMaxReportCount}";
                return Merge(emptySummary, debugCapture);
            }

            if (!IsUsableKhrResult(countResult))
                return Merge(
                    $"DeviceFaultKHR reportsResult={countResult}",
                    includeVendorBinary ? CaptureKhrDebugInfo(device, options) : null);

            int configuredCap = KhrDeviceFaultMaxReportCount == 0
                ? options.DeviceFaultReportCap
                : Math.Min(
                    options.DeviceFaultReportCap,
                    checked((int)Math.Min(KhrDeviceFaultMaxReportCount, int.MaxValue)));
            configuredCap = Math.Max(configuredCap, 1);
            VulkanKhrDeviceFaultInfo[] reports = new VulkanKhrDeviceFaultInfo[configuredCap];
            VulkanKhrDeviceFaultInfo[] batch = new VulkanKhrDeviceFaultInfo[Math.Min(ReportBatchSize, configuredCap)];
            uint firstAvailableCount = availableCount;
            uint remainingCount = availableCount;
            uint returnedCount = 0;
            Result reportsResult = Result.Success;
            bool incomplete = countResult != Result.Success;

            while (remainingCount > 0 && returnedCount < reports.Length)
            {
                uint writableCount = Math.Min(
                    remainingCount,
                    (uint)Math.Min(batch.Length, reports.Length - checked((int)returnedCount)));
                for (int index = 0; index < writableCount; index++)
                {
                    batch[index] = new VulkanKhrDeviceFaultInfo
                    {
                        SType = (StructureType)FaultInfoSType,
                    };
                }

                uint batchReturnedCount = writableCount;
                reportsResult = GetDeviceFaultReportsBatch(
                    device,
                    batch,
                    ref batchReturnedCount);

                uint initializedCount = Math.Min(batchReturnedCount, writableCount);
                if (initializedCount > 0)
                    Array.Copy(batch, 0, reports, returnedCount, initializedCount);
                returnedCount += initializedCount;
                incomplete |= reportsResult != Result.Success || batchReturnedCount > writableCount;
                if (!IsUsableKhrResult(reportsResult) || initializedCount == 0)
                    break;

                remainingCount = 0;
                Result nextCountResult = GetDeviceFaultReportsKhr(device, 0, &remainingCount, null);
                incomplete |= nextCountResult != Result.Success;
                if (nextCountResult == Result.Timeout || !IsUsableKhrResult(nextCountResult))
                    break;
            }

            if (returnedCount == reports.Length)
            {
                uint unavailableCount = 0;
                Result remainingResult = GetDeviceFaultReportsKhr(device, 0, &unavailableCount, null);
                if (IsUsableKhrResult(remainingResult))
                    remainingCount = unavailableCount;
                incomplete |= unavailableCount > 0 || remainingResult != Result.Success;
            }

            if (returnedCount < reports.Length)
                Array.Resize(ref reports, checked((int)returnedCount));

            byte[] reportArtifact = FormatReports(
                reports,
                returnedCount,
                countResult,
                reportsResult,
                incomplete,
                firstAvailableCount);
            string summary =
                $"DeviceFaultKHR active countResult={countResult} reportsResult={reportsResult} " +
                $"available={firstAvailableCount} returned={returnedCount} remainingOrTruncated={remainingCount} " +
                $"cap={configuredCap} incomplete={incomplete} artifact=vulkan-device-fault-khr-reports.log";

            VulkanDeviceFaultCapture reportsCapture = new(
                summary,
                new VulkanDeviceFaultArtifact("vulkan-device-fault-khr-reports.log", reportArtifact, false));
            return Merge(reportsCapture, includeVendorBinary ? CaptureKhrDebugInfo(device, options) : null);
        }
        catch (Exception exception)
        {
            return new VulkanDeviceFaultCapture(
                $"DeviceFaultKHR queryFailed={exception.GetType().Name}:{exception.Message}");
        }
    }

    /// <summary>Performs bounded EXT device-fault retrieval and formats persistence-ready artifacts.</summary>
    internal unsafe VulkanDeviceFaultCapture CaptureExt(
        ExtDeviceFault extension,
        Device device,
        in VulkanDiagnosticOptions options,
        bool khrExposed,
        bool vendorBinarySupported)
    {
        try
        {
            DeviceFaultCountsEXT counts = new()
            {
                SType = StructureType.DeviceFaultCountsExt,
            };
            Result countsResult = extension.GetDeviceFaultInfo(device, &counts, null);
            if (countsResult is not (Result.Success or Result.Incomplete))
            {
                return new VulkanDeviceFaultCapture(
                    $"DeviceFaultEXT countsResult={countsResult} khrExposed={khrExposed}");
            }

            uint reportedAddressCount = counts.AddressInfoCount;
            uint reportedVendorCount = counts.VendorInfoCount;
            ulong reportedBinarySize = counts.VendorBinarySize;
            uint writableAddressCount = Math.Min(reportedAddressCount, (uint)options.DeviceFaultAddressRecordCap);
            uint writableVendorCount = Math.Min(reportedVendorCount, (uint)options.DeviceFaultVendorRecordCap);
            ulong writableBinarySize = Math.Min(reportedBinarySize, (ulong)options.DeviceFaultVendorBinaryByteCap);
            bool recordsTruncated = writableAddressCount < reportedAddressCount ||
                writableVendorCount < reportedVendorCount;

            DeviceFaultAddressInfoEXT[] addresses = writableAddressCount == 0
                ? Array.Empty<DeviceFaultAddressInfoEXT>()
                : new DeviceFaultAddressInfoEXT[checked((int)writableAddressCount)];
            DeviceFaultVendorInfoEXT[] vendors = writableVendorCount == 0
                ? Array.Empty<DeviceFaultVendorInfoEXT>()
                : new DeviceFaultVendorInfoEXT[checked((int)writableVendorCount)];
            byte[]? vendorBinary = vendorBinarySupported && writableBinarySize > 0
                ? new byte[checked((int)writableBinarySize)]
                : null;
            string vendorBinaryStatus = !vendorBinarySupported
                ? "feature-disabled"
                : writableBinarySize == 0
                    ? "not-reported"
                    : writableBinarySize < reportedBinarySize
                        ? $"captured-truncated:{writableBinarySize}/{reportedBinarySize}"
                        : "captured";

            counts.AddressInfoCount = writableAddressCount;
            counts.VendorInfoCount = writableVendorCount;
            counts.VendorBinarySize = vendorBinary?.LongLength is > 0 ? writableBinarySize : 0;
            DeviceFaultInfoEXT info = new()
            {
                SType = StructureType.DeviceFaultInfoExt,
            };
            byte[] binaryBuffer = vendorBinary ?? Array.Empty<byte>();
            Result infoResult;
            string description;
            fixed (DeviceFaultAddressInfoEXT* addressPointer = addresses)
            fixed (DeviceFaultVendorInfoEXT* vendorPointer = vendors)
            fixed (byte* binaryPointer = binaryBuffer)
            {
                info.PAddressInfos = addresses.Length == 0 ? null : addressPointer;
                info.PVendorInfos = vendors.Length == 0 ? null : vendorPointer;
                info.PVendorBinaryData = binaryBuffer.Length == 0 ? null : binaryPointer;
                infoResult = extension.GetDeviceFaultInfo(device, &counts, &info);
                description = ReadUtf8(info.Description, VulkanKhrDeviceFaultNativeConstants.DescriptionBytes);
            }

            bool usable = infoResult is Result.Success or Result.Incomplete;
            if (!usable)
            {
                vendorBinary = null;
                vendorBinaryStatus = $"failed-unusable:{infoResult}";
            }
            bool incomplete = countsResult == Result.Incomplete ||
                infoResult != Result.Success ||
                recordsTruncated ||
                writableBinarySize < reportedBinarySize;
            byte[] report = FormatExtReport(
                description,
                counts,
                addresses,
                vendors,
                vendorBinary,
                countsResult,
                infoResult,
                incomplete,
                vendorBinaryStatus,
                khrExposed);
            string binaryFileName = vendorBinary is { Length: > 0 }
                ? $"vulkan-device-fault-vendor-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.bin"
                : string.Empty;
            string summary =
                $"DeviceFaultEXT countsResult={countsResult} infoResult={infoResult} incomplete={incomplete} " +
                $"addressInfos={counts.AddressInfoCount}/{reportedAddressCount} vendorInfos={counts.VendorInfoCount}/{reportedVendorCount} " +
                $"vendorBinaryBytes={writableBinarySize}/{reportedBinarySize} vendorBinary={vendorBinaryStatus} " +
                $"description='{Summarize(description)}' artifact=vulkan-device-fault-report.log " +
                $"vendorBinaryFile={(binaryFileName.Length == 0 ? "<none>" : binaryFileName)}";
            return binaryFileName.Length == 0
                ? new VulkanDeviceFaultCapture(
                    summary,
                    new VulkanDeviceFaultArtifact("vulkan-device-fault-report.log", report, false))
                : new VulkanDeviceFaultCapture(
                    summary,
                    new VulkanDeviceFaultArtifact("vulkan-device-fault-report.log", report, false),
                    new VulkanDeviceFaultArtifact(binaryFileName, vendorBinary!, true));
        }
        catch (Exception exception)
        {
            return new VulkanDeviceFaultCapture(
                $"DeviceFaultEXT queryFailed={exception.GetType().Name}:{exception.Message}");
        }
    }

    private unsafe VulkanDeviceFaultCapture CaptureKhrDebugInfo(
        Device device,
        in VulkanDiagnosticOptions options)
    {
        if (!SupportsKhrDeviceFaultVendorBinary || GetDeviceFaultDebugInfoKhr is null)
        {
            return new VulkanDeviceFaultCapture(
                "DeviceFaultKHRDebugInfo vendorBinary=feature-disabled-or-unavailable");
        }

        VulkanKhrDeviceFaultDebugInfo sizeInfo = new()
        {
            SType = (StructureType)FaultDebugInfoSType,
        };
        Result sizeResult = GetDeviceFaultDebugInfoKhr(device, &sizeInfo);
        uint vendorBinarySize = sizeInfo.VendorBinarySize;
        if (!IsUsableKhrResult(sizeResult))
            return new VulkanDeviceFaultCapture($"DeviceFaultKHRDebugInfo sizeResult={sizeResult}");
        if (vendorBinarySize == 0)
        {
            return new VulkanDeviceFaultCapture(
                $"DeviceFaultKHRDebugInfo sizeResult={sizeResult} vendorBinary=not-reported");
        }

        uint writableSize = Math.Min(vendorBinarySize, checked((uint)options.DeviceFaultVendorBinaryByteCap));
        byte[] vendorBinary = new byte[checked((int)writableSize)];
        Result dataResult;
        uint actualSize;
        fixed (byte* data = vendorBinary)
        {
            VulkanKhrDeviceFaultDebugInfo dataInfo = new()
            {
                SType = (StructureType)FaultDebugInfoSType,
                VendorBinarySize = writableSize,
                PVendorBinaryData = data,
            };
            dataResult = GetDeviceFaultDebugInfoKhr(device, &dataInfo);
            actualSize = dataInfo.VendorBinarySize;
        }

        bool usable = IsUsableKhrResult(dataResult);
        uint initializedSize = usable ? Math.Min(actualSize, writableSize) : 0;
        if (initializedSize < vendorBinary.Length)
            Array.Resize(ref vendorBinary, checked((int)initializedSize));
        bool incomplete = sizeResult != Result.Success ||
            dataResult != Result.Success ||
            vendorBinarySize > writableSize ||
            actualSize > writableSize;

        byte[] textArtifact = FormatDebugInfo(vendorBinary, sizeResult, dataResult, incomplete);
        string binaryFileName = vendorBinary.Length == 0
            ? string.Empty
            : $"vulkan-device-fault-khr-vendor-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.bin";
        string summary =
            $"DeviceFaultKHRDebugInfo sizeResult={sizeResult} dataResult={dataResult} " +
            $"vendorBinaryBytes={vendorBinary.Length}/{vendorBinarySize} cap={writableSize} " +
            $"status={(usable ? (incomplete ? "incomplete-or-truncated" : "complete") : "failed-unusable")} " +
            $"incomplete={incomplete} artifact=vulkan-device-fault-khr-debug-info.log " +
            $"vendorBinaryFile={(binaryFileName.Length == 0 ? "<none>" : binaryFileName)}";
        return binaryFileName.Length == 0
            ? new VulkanDeviceFaultCapture(
                summary,
                new VulkanDeviceFaultArtifact("vulkan-device-fault-khr-debug-info.log", textArtifact, false))
            : new VulkanDeviceFaultCapture(
                summary,
                new VulkanDeviceFaultArtifact("vulkan-device-fault-khr-debug-info.log", textArtifact, false),
                new VulkanDeviceFaultArtifact(binaryFileName, vendorBinary, true));
    }

    private static unsafe byte[] FormatReports(
        VulkanKhrDeviceFaultInfo[] reports,
        uint returnedCount,
        Result countResult,
        Result reportsResult,
        bool incomplete,
        uint availableCount)
    {
        StringBuilder report = new();
        report.AppendLine("Vulkan KHR Device Fault Reports");
        report.Append("Utc=").AppendLine(DateTimeOffset.UtcNow.ToString("O"));
        report.Append("CountResult=").Append(countResult)
            .Append(" ReportsResult=").Append(reportsResult)
            .Append(" Available=").Append(availableCount)
            .Append(" Returned=").Append(returnedCount)
            .Append(" Incomplete=").AppendLine(incomplete.ToString());

        int count = Math.Min(checked((int)returnedCount), reports.Length);
        for (int index = 0; index < count; index++)
            AppendKhrDeviceFaultReport(report, index, reports[index]);

        return Encoding.UTF8.GetBytes(report.ToString());
    }

    private unsafe Result GetDeviceFaultReportsBatch(
        Device device,
        VulkanKhrDeviceFaultInfo[] batch,
        ref uint returnedCount)
    {
        fixed (VulkanKhrDeviceFaultInfo* batchPointer = batch)
        fixed (uint* returnedCountPointer = &returnedCount)
            return GetDeviceFaultReportsKhr!(device, 0, returnedCountPointer, batchPointer);
    }

    private static unsafe void AppendKhrDeviceFaultReport(
        StringBuilder report,
        int index,
        VulkanKhrDeviceFaultInfo reportInfo)
    {
        VulkanKhrDeviceFaultInfo* info = &reportInfo;
        string description = ReadUtf8(
            info->Description,
            VulkanKhrDeviceFaultNativeConstants.DescriptionBytes);
        string vendorDescription = ReadUtf8(
            info->VendorInfo.Description,
            VulkanKhrDeviceFaultNativeConstants.DescriptionBytes);
        report.Append("Report[").Append(index).Append("] flags=").Append(info->Flags)
            .Append(" groupId=").Append(info->GroupId)
            .Append(" description=").AppendLine(string.IsNullOrWhiteSpace(description) ? "<empty>" : description);
        AppendAddress(report, "FaultAddress", info->FaultAddressInfo);
        AppendAddress(report, "InstructionAddress", info->InstructionAddressInfo);
        report.Append("Vendor code=0x").Append(info->VendorInfo.VendorFaultCode.ToString("X"))
            .Append(" data=0x").Append(info->VendorInfo.VendorFaultData.ToString("X"))
            .Append(" description=").AppendLine(string.IsNullOrWhiteSpace(vendorDescription) ? "<empty>" : vendorDescription);
    }

    private static unsafe byte[] FormatDebugInfo(
        byte[] vendorBinary,
        Result sizeResult,
        Result dataResult,
        bool incomplete)
    {
        StringBuilder report = new();
        report.AppendLine("Vulkan KHR Device Fault Debug Info");
        report.Append("Utc=").AppendLine(DateTimeOffset.UtcNow.ToString("O"));
        report.Append("SizeResult=").Append(sizeResult)
            .Append(" DataResult=").Append(dataResult)
            .Append(" Incomplete=").Append(incomplete)
            .Append(" VendorBinarySize=").AppendLine(vendorBinary.Length.ToString());

        if (vendorBinary.Length >= sizeof(VulkanKhrDeviceFaultVendorBinaryHeaderVersionOne))
        {
            fixed (byte* data = vendorBinary)
            {
                VulkanKhrDeviceFaultVendorBinaryHeaderVersionOne* header =
                    (VulkanKhrDeviceFaultVendorBinaryHeaderVersionOne*)data;
                report.Append("VendorBinaryHeader headerSize=").Append(header->HeaderSize)
                    .Append(" version=").Append(header->HeaderVersion)
                    .Append(" vendor=0x").Append(header->VendorID.ToString("X"))
                    .Append(" device=0x").Append(header->DeviceID.ToString("X"))
                    .Append(" driver=0x").Append(header->DriverVersion.ToString("X"))
                    .Append(" api=0x").Append(header->ApiVersion.ToString("X"))
                    .AppendLine();
            }
        }

        return Encoding.UTF8.GetBytes(report.ToString());
    }

    private byte[] FormatExtReport(
        string description,
        in DeviceFaultCountsEXT counts,
        DeviceFaultAddressInfoEXT[] addresses,
        DeviceFaultVendorInfoEXT[] vendors,
        byte[]? vendorBinary,
        Result countsResult,
        Result infoResult,
        bool incomplete,
        string vendorBinaryStatus,
        bool khrExposed)
    {
        StringBuilder report = new();
        report.AppendLine("Vulkan Device Fault Report");
        report.Append("Utc=").AppendLine(DateTimeOffset.UtcNow.ToString("O"));
        report.Append("KHR device fault exposed=").Append(khrExposed)
            .Append(" active=").Append(IsUsingKhrDeviceFault)
            .Append(" functionTable=").Append(GetDeviceFaultReportsKhr is not null)
            .AppendLine();
        report.Append("CountsResult=").Append(countsResult)
            .Append(" InfoResult=").Append(infoResult)
            .Append(" Incomplete=").AppendLine(incomplete.ToString());
        report.Append("Description=").AppendLine(string.IsNullOrWhiteSpace(description) ? "<empty>" : description);
        report.Append("AddressInfoCount=").Append(counts.AddressInfoCount)
            .Append(" VendorInfoCount=").Append(counts.VendorInfoCount)
            .Append(" VendorBinarySize=").Append(counts.VendorBinarySize)
            .Append(" VendorBinaryStatus=").AppendLine(vendorBinaryStatus);

        for (int index = 0; index < addresses.Length; index++)
        {
            DeviceFaultAddressInfoEXT address = addresses[index];
            report.Append("Address[").Append(index).Append("] type=").Append(address.AddressType)
                .Append(" reported=0x").Append(address.ReportedAddress.ToString("X"))
                .Append(" precision=0x").Append(address.AddressPrecision.ToString("X"))
                .AppendLine();
        }
        unsafe
        {
            for (int index = 0; index < vendors.Length; index++)
            {
                DeviceFaultVendorInfoEXT vendor = vendors[index];
                string vendorDescription = ReadUtf8(vendor.Description, VulkanKhrDeviceFaultNativeConstants.DescriptionBytes);
                report.Append("Vendor[").Append(index).Append("] code=0x").Append(vendor.VendorFaultCode.ToString("X"))
                    .Append(" data=0x").Append(vendor.VendorFaultData.ToString("X"))
                    .Append(" description=").AppendLine(string.IsNullOrWhiteSpace(vendorDescription) ? "<empty>" : vendorDescription);
            }

            if (vendorBinary is { Length: >= 1 } &&
                vendorBinary.Length >= sizeof(DeviceFaultVendorBinaryHeaderVersionOneEXT))
            {
                fixed (byte* binaryPointer = vendorBinary)
                {
                    DeviceFaultVendorBinaryHeaderVersionOneEXT* header =
                        (DeviceFaultVendorBinaryHeaderVersionOneEXT*)binaryPointer;
                    report.Append("VendorBinaryHeader headerSize=").Append(header->HeaderSize)
                        .Append(" version=").Append(header->HeaderVersion)
                        .Append(" vendor=0x").Append(header->VendorID.ToString("X"))
                        .Append(" device=0x").Append(header->DeviceID.ToString("X"))
                        .Append(" driver=0x").Append(header->DriverVersion.ToString("X"))
                        .Append(" api=0x").Append(header->ApiVersion.ToString("X"))
                        .AppendLine();
                }
            }
        }

        return Encoding.UTF8.GetBytes(report.ToString());
    }

    private static string Summarize(string value)
    {
        const int maxLength = 160;
        string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : string.Concat(normalized.AsSpan(0, maxLength), "...");
    }

    private static VulkanDeviceFaultCapture Merge(string summary, VulkanDeviceFaultCapture? second)
        => second is null
            ? new VulkanDeviceFaultCapture(summary)
            : Merge(new VulkanDeviceFaultCapture(summary), second);

    private static VulkanDeviceFaultCapture Merge(
        VulkanDeviceFaultCapture first,
        VulkanDeviceFaultCapture? second)
    {
        if (second is null)
            return first;

        VulkanDeviceFaultArtifact[] artifacts = new VulkanDeviceFaultArtifact[
            first.Artifacts.Length + second.Artifacts.Length];
        first.Artifacts.CopyTo(artifacts);
        second.Artifacts.CopyTo(artifacts.AsSpan(first.Artifacts.Length));
        return new VulkanDeviceFaultCapture(
            $"{first.Summary}{Environment.NewLine}{second.Summary}",
            artifacts);
    }

    private static bool IsUsableKhrResult(Result result)
        => result is Result.Success or Result.Incomplete || result == ErrorNotEnoughSpaceKhr;

    private static void AppendAddress(
        StringBuilder report,
        string label,
        in VulkanKhrDeviceFaultAddressInfo info)
        => report.Append(label).Append(" type=").Append(info.AddressType)
            .Append(" reported=0x").Append(info.ReportedAddress.ToString("X"))
            .Append(" precision=0x").Append(info.AddressPrecision.ToString("X"))
            .AppendLine();

    private static unsafe string ReadUtf8(byte* bytes, int capacity)
    {
        int length = 0;
        while (length < capacity && bytes[length] != 0)
            length++;
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes, length);
    }
}

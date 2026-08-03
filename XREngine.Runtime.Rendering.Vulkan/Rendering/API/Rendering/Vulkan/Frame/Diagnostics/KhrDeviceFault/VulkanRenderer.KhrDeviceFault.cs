using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const uint VulkanKhrDeviceFaultPhysicalDeviceFeaturesSType = 1000573000;
    private const uint VulkanKhrDeviceFaultPhysicalDevicePropertiesSType = 1000573001;
    private const uint VulkanKhrDeviceFaultInfoSType = 1000573002;
    private const uint VulkanKhrDeviceFaultDebugInfoSType = 1000573003;
    private const int KhrDeviceFaultReportBatchSize = 16;
    private const Result VulkanErrorNotEnoughSpaceKhr = (Result)(-1000483000);

    private VkGetDeviceFaultReportsKhrDelegate? _vkGetDeviceFaultReportsKHR;
    private VkGetDeviceFaultDebugInfoKhrDelegate? _vkGetDeviceFaultDebugInfoKHR;
    private bool _supportsKhrDeviceFault;
    private bool _supportsKhrDeviceFaultVendorBinary;
    private bool _supportsKhrDeviceFaultReportMasked;
    private bool _supportsKhrDeviceFaultDeviceLostOnMasked;
    private uint _khrDeviceFaultMaxReportCount;
    private bool _supportsExtDeviceFault;
    private bool _supportsExtDeviceFaultVendorBinary;
    private bool _deviceFaultUsingKhr;

    /// <summary>
    /// Retrieves the KHR device fault reports for the specified device within the given timeout period.
    /// </summary>
    /// <param name="device">The logical device to query for fault reports.</param>
    /// <param name="timeout">The maximum time to wait for fault reports, in nanoseconds.</param>
    /// <param name="pFaultCounts">A pointer to a variable that will receive the number of fault reports.</param>
    /// <param name="pFaultInfo">A pointer to an array of VulkanKhrDeviceFaultInfo structures that will receive the fault details.</param>
    /// <returns>A Result indicating success or failure of the operation.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate Result VkGetDeviceFaultReportsKhrDelegate(
        Device device,
        ulong timeout,
        uint* pFaultCounts,
        VulkanKhrDeviceFaultInfo* pFaultInfo);

    /// <summary>
    /// Retrieves the debug information for a KHR device fault on the specified device.
    /// </summary>
    /// <param name="device">The logical device to query for fault debug information.</param>
    /// <param name="pDebugInfo">A pointer to a VulkanKhrDeviceFaultDebugInfo structure that will receive the debug information.</param>
    /// <returns>A Result indicating success or failure of the operation.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate Result VkGetDeviceFaultDebugInfoKhrDelegate(
        Device device,
        VulkanKhrDeviceFaultDebugInfo* pDebugInfo);

    /// <summary>
    /// Converts a raw uint value to the corresponding Vulkan StructureType.
    /// </summary>
    /// <param name="value">The raw uint value representing a Vulkan structure type.</param>
    /// <returns>The corresponding Vulkan StructureType.</returns>
    private static StructureType KhrStructureType(uint value)
        => (StructureType)value;

    /// <summary>
    /// Converts a boolean value to its Vulkan representation (1 for true, 0 for false).
    /// </summary>
    /// <param name="value">The boolean value to convert.</param>
    /// <returns>1 if the value is true; 0 if the value is false.</returns>
    private static uint ToVulkanBool(bool value)
        => value ? 1u : 0u;

    /// <summary>
    /// Attempts to load the function pointers for the KHR device fault extension.
    /// </summary>
    /// <returns>True if the function pointers were successfully loaded; otherwise, false.</returns>
    private bool TryLoadKhrDeviceFaultFunctionPointers()
    {
        _vkGetDeviceFaultReportsKHR = null;
        _vkGetDeviceFaultDebugInfoKHR = null;

        if (!_supportsKhrDeviceFault || device.Handle == 0)
            return false;

        nint reportsProc = (nint)Api!.GetDeviceProcAddr(device, "vkGetDeviceFaultReportsKHR");
        nint debugInfoProc = (nint)Api.GetDeviceProcAddr(device, "vkGetDeviceFaultDebugInfoKHR");
        if (reportsProc == 0 || debugInfoProc == 0)
        {
            Debug.VulkanWarning(
                "[VulkanDiag] KHR advertised but function pointer unavailable: reports=0x{0:X} debugInfo=0x{1:X}.",
                reportsProc,
                debugInfoProc);
            return false;
        }

        _vkGetDeviceFaultReportsKHR = Marshal.GetDelegateForFunctionPointer<VkGetDeviceFaultReportsKhrDelegate>(reportsProc);
        _vkGetDeviceFaultDebugInfoKHR = Marshal.GetDelegateForFunctionPointer<VkGetDeviceFaultDebugInfoKhrDelegate>(debugInfoProc);
        _deviceFaultUsingKhr = true;
        Debug.Vulkan(
            "[VulkanDiag] DeviceFaultKHR active reports=0x{0:X} debugInfo=0x{1:X} vendorBinary={2} maskedReports={3} lostOnMasked={4}.",
            reportsProc,
            debugInfoProc,
            _supportsKhrDeviceFaultVendorBinary,
            _supportsKhrDeviceFaultReportMasked,
            _supportsKhrDeviceFaultDeviceLostOnMasked);
        return true;
    }

    /// <summary>
    /// Releases the function pointers for the KHR device fault extension.
    /// </summary>
    private void ReleaseKhrDeviceFaultFunctionPointers()
    {
        _vkGetDeviceFaultReportsKHR = null;
        _vkGetDeviceFaultDebugInfoKHR = null;
        _deviceFaultUsingKhr = false;
    }

    /// <summary>
    /// Queries the capabilities of the KHR device fault extension for the current physical device.
    /// </summary>
    /// <param name="extensionEnabled">Indicates whether the KHR device fault extension is enabled.</param>
    /// <param name="deviceFaultSupported">Outputs whether device fault reporting is supported.</param>
    /// <param name="vendorBinarySupported">Outputs whether vendor binary support is available for device faults.</param>
    /// <param name="reportMaskedSupported">Outputs whether masked reporting is supported for device faults.</param>
    /// <param name="deviceLostOnMaskedSupported">Outputs whether the device can be lost on masked faults.</param>
    /// <param name="maxReportCount">Outputs the maximum number of device fault reports supported.</param>
    private unsafe void QueryKhrDeviceFaultCapabilities(
        bool extensionEnabled,
        out bool deviceFaultSupported,
        out bool vendorBinarySupported,
        out bool reportMaskedSupported,
        out bool deviceLostOnMaskedSupported,
        out uint maxReportCount)
    {
        deviceFaultSupported = false;
        vendorBinarySupported = false;
        reportMaskedSupported = false;
        deviceLostOnMaskedSupported = false;
        maxReportCount = 0;
        if (!extensionEnabled)
            return;

        VulkanKhrPhysicalDeviceFaultFeatures features = new()
        {
            SType = KhrStructureType(VulkanKhrDeviceFaultPhysicalDeviceFeaturesSType),
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        deviceFaultSupported = features.DeviceFault != 0;
        vendorBinarySupported = features.DeviceFaultVendorBinary != 0;
        reportMaskedSupported = features.DeviceFaultReportMasked != 0;
        deviceLostOnMaskedSupported = features.DeviceFaultDeviceLostOnMasked != 0;

        VulkanKhrPhysicalDeviceFaultProperties properties = new()
        {
            SType = KhrStructureType(VulkanKhrDeviceFaultPhysicalDevicePropertiesSType),
            PNext = null,
        };

        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &properties,
        };

        Api.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        maxReportCount = properties.MaxDeviceFaultCount;
    }

    /// <summary>
    /// Attempts to append a summary of the KHR device fault reports to the provided StringBuilder.
    /// </summary>
    /// <param name="builder">The StringBuilder to append the summary to.</param>
    /// <returns>True if a summary was appended, false otherwise.</returns>
    private bool TryAppendKhrDeviceFaultSummary(StringBuilder builder)
    {
        if (!_supportsKhrDeviceFault)
            return false;

        if (_vkGetDeviceFaultReportsKHR is null)
        {
            AppendFaultSection(builder, "KHR advertised but function pointer unavailable");
            return false;
        }

        try
        {
            uint availableCount = 0;
            Result countResult = _vkGetDeviceFaultReportsKHR(device, 0, &availableCount, null);

            if (countResult == Result.Timeout || availableCount == 0)
            {
                AppendFaultSection(
                    builder,
                    $"DeviceFaultKHR active reports=0 countResult={countResult} maxReports={_khrDeviceFaultMaxReportCount}");
                if (_deviceLost)
                    TryAppendKhrDeviceFaultDebugInfo(builder);
                return true;
            }

            if (!IsKhrDeviceFaultPartialResult(countResult))
            {
                AppendFaultSection(builder, $"DeviceFaultKHR reportsResult={countResult}");
                if (_deviceLost)
                    TryAppendKhrDeviceFaultDebugInfo(builder);
                return true;
            }

            int configuredCap = _khrDeviceFaultMaxReportCount == 0
                ? _diagnosticOptions.DeviceFaultReportCap
                : Math.Min(_diagnosticOptions.DeviceFaultReportCap, checked((int)Math.Min(_khrDeviceFaultMaxReportCount, int.MaxValue)));
            VulkanKhrDeviceFaultInfo[] reports = new VulkanKhrDeviceFaultInfo[configuredCap];
            VulkanKhrDeviceFaultInfo[] batch = new VulkanKhrDeviceFaultInfo[Math.Min(KhrDeviceFaultReportBatchSize, configuredCap)];
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
                for (int i = 0; i < writableCount; i++)
                {
                    batch[i] = new()
                    {
                        SType = KhrStructureType(VulkanKhrDeviceFaultInfoSType),
                        PNext = null,
                    };
                }

                uint batchReturnedCount = writableCount;
                fixed (VulkanKhrDeviceFaultInfo* batchPtr = batch)
                    reportsResult = _vkGetDeviceFaultReportsKHR(device, 0, &batchReturnedCount, batchPtr);

                uint initializedCount = Math.Min(batchReturnedCount, writableCount);
                if (initializedCount > 0)
                    Array.Copy(batch, 0, reports, returnedCount, initializedCount);
                returnedCount += initializedCount;
                incomplete |= reportsResult != Result.Success || batchReturnedCount > writableCount;

                if (!IsKhrDeviceFaultPartialResult(reportsResult) || initializedCount == 0)
                    break;

                remainingCount = 0;
                Result nextCountResult = _vkGetDeviceFaultReportsKHR(device, 0, &remainingCount, null);
                incomplete |= nextCountResult != Result.Success;
                if (nextCountResult == Result.Timeout || !IsKhrDeviceFaultPartialResult(nextCountResult))
                    break;
            }

            if (returnedCount == reports.Length)
            {
                uint unavailableCount = 0;
                Result remainingResult = _vkGetDeviceFaultReportsKHR(device, 0, &unavailableCount, null);
                if (IsKhrDeviceFaultPartialResult(remainingResult))
                    remainingCount = unavailableCount;
                incomplete |= unavailableCount > 0 || remainingResult != Result.Success;
            }

            if (returnedCount < reports.Length)
                Array.Resize(ref reports, checked((int)returnedCount));

            string artifactSummary = PersistKhrDeviceFaultReports(
                reports,
                returnedCount,
                countResult,
                reportsResult,
                incomplete,
                firstAvailableCount);

            AppendFaultSection(
                builder,
                $"DeviceFaultKHR active countResult={countResult} reportsResult={reportsResult} " +
                $"available={firstAvailableCount} returned={returnedCount} remainingOrTruncated={remainingCount} " +
                $"cap={configuredCap} incomplete={incomplete} {artifactSummary}");

            if (_deviceLost)
                TryAppendKhrDeviceFaultDebugInfo(builder);

            return true;
        }
        catch (Exception ex)
        {
            AppendFaultSection(builder, $"DeviceFaultKHR queryFailed={ex.GetType().Name}:{ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Determines whether the specified Vulkan result indicates a partial result for the KHR device fault extension.
    /// </summary>
    /// <param name="result">The Vulkan result to check.</param>
    /// <returns>True if the result indicates a partial result for the KHR device fault extension; otherwise, false.</returns>
    private static bool IsKhrDeviceFaultPartialResult(Result result)
        => result is Result.Success or Result.Incomplete || result == VulkanErrorNotEnoughSpaceKhr;

    /// <summary>
    /// Attempts to append the KHR device fault debug information to the provided StringBuilder.
    /// </summary>
    /// <param name="builder">The StringBuilder to append the debug information to.</param>
    private void TryAppendKhrDeviceFaultDebugInfo(StringBuilder builder)
    {
        if (!_supportsKhrDeviceFaultVendorBinary || _vkGetDeviceFaultDebugInfoKHR is null)
        {
            AppendFaultSection(builder, "DeviceFaultKHRDebugInfo vendorBinary=feature-disabled-or-unavailable");
            return;
        }

        VulkanKhrDeviceFaultDebugInfo sizeInfo = new()
        {
            SType = KhrStructureType(VulkanKhrDeviceFaultDebugInfoSType),
            PNext = null,
            VendorBinarySize = 0,
            PVendorBinaryData = null,
        };

        Result sizeResult = _vkGetDeviceFaultDebugInfoKHR(device, &sizeInfo);
        uint vendorBinarySize = sizeInfo.VendorBinarySize;
        if (!IsKhrDeviceFaultPartialResult(sizeResult))
        {
            AppendFaultSection(builder, $"DeviceFaultKHRDebugInfo sizeResult={sizeResult}");
            return;
        }

        if (vendorBinarySize == 0)
        {
            AppendFaultSection(builder, $"DeviceFaultKHRDebugInfo sizeResult={sizeResult} vendorBinary=not-reported");
            return;
        }

        uint writableVendorBinarySize = Math.Min(vendorBinarySize, checked((uint)_diagnosticOptions.DeviceFaultVendorBinaryByteCap));
        byte[] vendorBinary = new byte[checked((int)writableVendorBinarySize)];
        Result dataResult;
        uint actualVendorBinarySize;
        fixed (byte* vendorBinaryPtr = vendorBinary)
        {
            VulkanKhrDeviceFaultDebugInfo dataInfo = new()
            {
                SType = KhrStructureType(VulkanKhrDeviceFaultDebugInfoSType),
                PNext = null,
                VendorBinarySize = writableVendorBinarySize,
                PVendorBinaryData = vendorBinaryPtr,
            };
            dataResult = _vkGetDeviceFaultDebugInfoKHR(device, &dataInfo);
            actualVendorBinarySize = dataInfo.VendorBinarySize;
        }

        bool dataResultUsable = IsKhrDeviceFaultPartialResult(dataResult);
        uint initializedVendorBinarySize = dataResultUsable
            ? Math.Min(actualVendorBinarySize, writableVendorBinarySize)
            : 0u;
        if (initializedVendorBinarySize < vendorBinary.Length)
            Array.Resize(ref vendorBinary, checked((int)initializedVendorBinarySize));

        bool incomplete = sizeResult != Result.Success ||
            dataResult != Result.Success ||
            vendorBinarySize > writableVendorBinarySize ||
            actualVendorBinarySize > writableVendorBinarySize;
        string artifactSummary = PersistKhrDeviceFaultDebugInfo(vendorBinary, sizeResult, dataResult, incomplete);
        AppendFaultSection(
            builder,
            $"DeviceFaultKHRDebugInfo sizeResult={sizeResult} dataResult={dataResult} " +
            $"vendorBinaryBytes={vendorBinary.Length}/{vendorBinarySize} cap={writableVendorBinarySize} " +
            $"status={(dataResultUsable ? (incomplete ? "incomplete-or-truncated" : "complete") : "failed-unusable")} " +
            $"incomplete={incomplete} {artifactSummary}");
    }

    /// <summary>
    /// Persists the KHR device fault reports to a string for diagnostic purposes.
    /// </summary>
    /// <param name="reports">The array of KHR device fault reports.</param>
    /// <param name="returnedCount">The number of reports returned by the Vulkan API.</param>
    /// <param name="countResult">The Vulkan result of the count query.</param>
    /// <param name="reportsResult">The Vulkan result of the reports query.</param>
    /// <param name="incomplete">Indicates whether the reports are incomplete.</param>
    /// <param name="availableCount">The total number of available reports.</param>
    /// <returns>A string containing the persisted KHR device fault reports for diagnostic purposes.</returns>
    private string PersistKhrDeviceFaultReports(
        VulkanKhrDeviceFaultInfo[] reports,
        uint returnedCount,
        Result countResult,
        Result reportsResult,
        bool incomplete,
        uint availableCount)
    {
        try
        {
            StringBuilder report = new();
            report.AppendLine("Vulkan KHR Device Fault Reports");
            report.Append("Utc=").AppendLine(DateTimeOffset.UtcNow.ToString("O"));
            report.Append("CountResult=").Append(countResult)
                .Append(" ReportsResult=").Append(reportsResult)
                .Append(" Available=").Append(availableCount)
                .Append(" Returned=").Append(returnedCount)
                .Append(" Incomplete=").AppendLine(incomplete.ToString());

            fixed (VulkanKhrDeviceFaultInfo* reportsPtr = reports)
            {
                int count = Math.Min(checked((int)returnedCount), reports.Length);
                for (int i = 0; i < count; i++)
                {
                    VulkanKhrDeviceFaultInfo* info = reportsPtr + i;
                    string description = ReadNullTerminatedUtf8(info->Description, VulkanDeviceFaultDescriptionBytes);
                    string vendorDescription = ReadNullTerminatedUtf8(info->VendorInfo.Description, VulkanDeviceFaultDescriptionBytes);

                    report
                        .Append("Report[").Append(i).Append("] flags=").Append(info->Flags)
                        .Append(" groupId=").Append(info->GroupId)
                        .Append(" description=").AppendLine(string.IsNullOrWhiteSpace(description) ? "<empty>" : description);
                    AppendKhrDeviceFaultAddressInfo(report, "FaultAddress", info->FaultAddressInfo);
                    AppendKhrDeviceFaultAddressInfo(report, "InstructionAddress", info->InstructionAddressInfo);
                    report
                        .Append("Vendor code=0x").Append(info->VendorInfo.VendorFaultCode.ToString("X"))
                        .Append(" data=0x").Append(info->VendorInfo.VendorFaultData.ToString("X"))
                        .Append(" description=").AppendLine(string.IsNullOrWhiteSpace(vendorDescription) ? "<empty>" : vendorDescription);
                }
            }

            const string reportFileName = "vulkan-device-fault-khr-reports.log";
            Debug.WriteAuxiliaryLog(reportFileName, report.ToString());
            return $"artifact={reportFileName}";
        }
        catch (Exception ex)
        {
            return $"artifactWriteFailed={ex.GetType().Name}:{ex.Message}";
        }
    }

    /// <summary>
    /// Persists the KHR device fault debug information to a string for diagnostic purposes.
    /// </summary>
    /// <param name="vendorBinary">The vendor-specific binary data associated with the device fault.</param>
    /// <param name="sizeResult">The Vulkan result of the size query for the vendor binary.</param>
    /// <param name="dataResult">The Vulkan result of the data query for the vendor binary.</param>
    /// <param name="incomplete">Indicates whether the vendor binary data is incomplete.</param>
    /// <returns>A string containing the persisted KHR device fault debug information for diagnostic purposes.</returns>
    private string PersistKhrDeviceFaultDebugInfo(
        byte[] vendorBinary,
        Result sizeResult,
        Result dataResult,
        bool incomplete)
    {
        try
        {
            StringBuilder report = new();
            report.AppendLine("Vulkan KHR Device Fault Debug Info");
            report.Append("Utc=").AppendLine(DateTimeOffset.UtcNow.ToString("O"));
            report.Append("SizeResult=").Append(sizeResult)
                .Append(" DataResult=").Append(dataResult)
                .Append(" Incomplete=").Append(incomplete)
                .Append(" VendorBinarySize=").AppendLine(vendorBinary.Length.ToString());
            AppendKhrVendorBinaryHeader(report, vendorBinary);

            const string reportFileName = "vulkan-device-fault-khr-debug-info.log";
            Debug.WriteAuxiliaryLog(reportFileName, report.ToString());

            string binarySummary = "vendorBinaryFile=<none>";
            if (vendorBinary.Length > 0)
            {
                string directory = Debug.EnsureLogRunDirectory();
                string binaryFileName = $"vulkan-device-fault-khr-vendor-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.bin";
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
    /// Appends the KHR device fault address information to the given report.
    /// </summary>
    /// <param name="report">The StringBuilder to which the address information will be appended.</param>
    /// <param name="label">A label identifying the address information being appended.</param>
    /// <param name="info">The VulkanKhrDeviceFaultAddressInfo structure containing the address information.</param>
    private void AppendKhrDeviceFaultAddressInfo(
        StringBuilder report,
        string label,
        in VulkanKhrDeviceFaultAddressInfo info)
    {
        report
            .Append(label).Append(" type=").Append(info.AddressType)
            .Append(" reported=0x").Append(info.ReportedAddress.ToString("X"))
            .Append(" precision=0x").Append(info.AddressPrecision.ToString("X"))
            .Append(" object=").Append(DescribeVulkanAddressCorrelation(info.ReportedAddress) ?? "<untracked>")
            .AppendLine();
    }

    /// <summary>
    /// Appends the KHR vendor binary header information to the given report.
    /// </summary>
    /// <param name="report">The StringBuilder to which the vendor binary header information will be appended.</param>
    /// <param name="vendorBinary">The vendor-specific binary data containing the header information.</param>
    private static void AppendKhrVendorBinaryHeader(StringBuilder report, byte[] vendorBinary)
    {
        if (vendorBinary.Length < sizeof(VulkanKhrDeviceFaultVendorBinaryHeaderVersionOne))
            return;

        fixed (byte* binaryPtr = vendorBinary)
        {
            VulkanKhrDeviceFaultVendorBinaryHeaderVersionOne* header = (VulkanKhrDeviceFaultVendorBinaryHeaderVersionOne*)binaryPtr;
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
}

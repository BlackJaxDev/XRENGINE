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
    private const int MaxKhrDeviceFaultReports = 64;

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

    [Flags]
    private enum VulkanKhrDeviceFaultFlags : uint
    {
        DeviceLost = 0x00000001,
        MemoryAddress = 0x00000002,
        InstructionAddress = 0x00000004,
        Vendor = 0x00000008,
        WatchdogTimeout = 0x00000010,
        Overflow = 0x00000020,
    }

    private enum VulkanKhrDeviceFaultAddressType : int
    {
        None = 0,
        ReadInvalid = 1,
        WriteInvalid = 2,
        ExecuteInvalid = 3,
        InstructionPointerUnknown = 4,
        InstructionPointerInvalid = 5,
        InstructionPointerFault = 6,
    }

    private enum VulkanKhrDeviceFaultVendorBinaryHeaderVersion : int
    {
        One = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanKhrPhysicalDeviceFaultFeatures
    {
        public StructureType SType;
        public void* PNext;
        public uint DeviceFault;
        public uint DeviceFaultVendorBinary;
        public uint DeviceFaultReportMasked;
        public uint DeviceFaultDeviceLostOnMasked;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanKhrPhysicalDeviceFaultProperties
    {
        public StructureType SType;
        public void* PNext;
        public uint MaxDeviceFaultCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanKhrDeviceFaultAddressInfo
    {
        public VulkanKhrDeviceFaultAddressType AddressType;
        public ulong ReportedAddress;
        public ulong AddressPrecision;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VulkanKhrDeviceFaultVendorInfo
    {
        public fixed byte Description[VulkanDeviceFaultDescriptionBytes];
        public ulong VendorFaultCode;
        public ulong VendorFaultData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VulkanKhrDeviceFaultInfo
    {
        public StructureType SType;
        public void* PNext;
        public VulkanKhrDeviceFaultFlags Flags;
        public ulong GroupId;
        public fixed byte Description[VulkanDeviceFaultDescriptionBytes];
        public VulkanKhrDeviceFaultAddressInfo FaultAddressInfo;
        public VulkanKhrDeviceFaultAddressInfo InstructionAddressInfo;
        public VulkanKhrDeviceFaultVendorInfo VendorInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VulkanKhrDeviceFaultDebugInfo
    {
        public StructureType SType;
        public void* PNext;
        public uint VendorBinarySize;
        public void* PVendorBinaryData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VulkanKhrDeviceFaultVendorBinaryHeaderVersionOne
    {
        public uint HeaderSize;
        public VulkanKhrDeviceFaultVendorBinaryHeaderVersion HeaderVersion;
        public uint VendorID;
        public uint DeviceID;
        public uint DriverVersion;
        public fixed byte PipelineCacheUUID[16];
        public uint ApplicationNameOffset;
        public uint ApplicationVersion;
        public uint EngineNameOffset;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate Result VkGetDeviceFaultReportsKhrDelegate(
        Device device,
        ulong timeout,
        uint* pFaultCounts,
        VulkanKhrDeviceFaultInfo* pFaultInfo);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate Result VkGetDeviceFaultDebugInfoKhrDelegate(
        Device device,
        VulkanKhrDeviceFaultDebugInfo* pDebugInfo);

    private static StructureType KhrStructureType(uint value)
        => (StructureType)value;

    private static uint ToVulkanBool(bool value)
        => value ? 1u : 0u;

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

    private void ReleaseKhrDeviceFaultFunctionPointers()
    {
        _vkGetDeviceFaultReportsKHR = null;
        _vkGetDeviceFaultDebugInfoKHR = null;
        _deviceFaultUsingKhr = false;
    }

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

            if (countResult is not (Result.Success or Result.Incomplete))
            {
                AppendFaultSection(builder, $"DeviceFaultKHR reportsResult={countResult}");
                if (_deviceLost)
                    TryAppendKhrDeviceFaultDebugInfo(builder);
                return true;
            }

            uint writableCount = Math.Min(availableCount, MaxKhrDeviceFaultReports);
            VulkanKhrDeviceFaultInfo[] reports = new VulkanKhrDeviceFaultInfo[checked((int)writableCount)];
            for (int i = 0; i < reports.Length; i++)
            {
                reports[i] = new()
                {
                    SType = KhrStructureType(VulkanKhrDeviceFaultInfoSType),
                    PNext = null,
                };
            }

            Result reportsResult;
            uint returnedCount = writableCount;
            fixed (VulkanKhrDeviceFaultInfo* reportsPtr = reports)
            {
                reportsResult = _vkGetDeviceFaultReportsKHR(device, 0, &returnedCount, reportsPtr);
            }

            bool incomplete = countResult == Result.Incomplete ||
                reportsResult == Result.Incomplete ||
                availableCount > writableCount;
            string artifactSummary = PersistKhrDeviceFaultReports(
                reports,
                returnedCount,
                countResult,
                reportsResult,
                incomplete,
                availableCount);

            AppendFaultSection(
                builder,
                $"DeviceFaultKHR active countResult={countResult} reportsResult={reportsResult} " +
                $"available={availableCount} returned={returnedCount} incomplete={incomplete} {artifactSummary}");

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
        if (sizeResult is not (Result.Success or Result.Incomplete))
        {
            AppendFaultSection(builder, $"DeviceFaultKHRDebugInfo sizeResult={sizeResult}");
            return;
        }

        if (vendorBinarySize == 0)
        {
            AppendFaultSection(builder, $"DeviceFaultKHRDebugInfo sizeResult={sizeResult} vendorBinary=not-reported");
            return;
        }

        byte[] vendorBinary = new byte[checked((int)vendorBinarySize)];
        Result dataResult;
        uint actualVendorBinarySize;
        fixed (byte* vendorBinaryPtr = vendorBinary)
        {
            VulkanKhrDeviceFaultDebugInfo dataInfo = new()
            {
                SType = KhrStructureType(VulkanKhrDeviceFaultDebugInfoSType),
                PNext = null,
                VendorBinarySize = vendorBinarySize,
                PVendorBinaryData = vendorBinaryPtr,
            };
            dataResult = _vkGetDeviceFaultDebugInfoKHR(device, &dataInfo);
            actualVendorBinarySize = dataInfo.VendorBinarySize;
        }

        if (actualVendorBinarySize < vendorBinary.Length)
            Array.Resize(ref vendorBinary, checked((int)actualVendorBinarySize));

        bool incomplete = sizeResult == Result.Incomplete || dataResult == Result.Incomplete;
        string artifactSummary = PersistKhrDeviceFaultDebugInfo(vendorBinary, sizeResult, dataResult, incomplete);
        AppendFaultSection(
            builder,
            $"DeviceFaultKHRDebugInfo sizeResult={sizeResult} dataResult={dataResult} " +
            $"vendorBinaryBytes={vendorBinary.Length} incomplete={incomplete} {artifactSummary}");
    }

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

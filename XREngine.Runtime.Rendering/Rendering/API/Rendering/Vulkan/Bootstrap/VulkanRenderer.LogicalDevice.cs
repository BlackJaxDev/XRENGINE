using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.NV;
using System.Text;
using System.Runtime.CompilerServices;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private const uint VulkanApiVersion14 = (1u << 22) | (4u << 12);
    private const string ExtDeviceFaultExtensionName = "VK_EXT_device_fault";
    private const string KhrDeviceFaultExtensionName = "VK_KHR_device_fault";
    private const string ExtDeviceAddressBindingReportExtensionName = "VK_EXT_device_address_binding_report";
    private const string NvDeviceDiagnosticCheckpointsExtensionName = "VK_NV_device_diagnostic_checkpoints";
    private const string NvDeviceDiagnosticsConfigExtensionName = "VK_NV_device_diagnostics_config";

    private bool _supportsAnisotropy;
    private string[] _availableDeviceExtensions = Array.Empty<string>();
    private string[] _enabledDeviceExtensions = Array.Empty<string>();
    private Device device;
    private bool _vulkanDeviceCreatedThroughOpenXr;
    private Queue graphicsQueue;
    private Queue secondaryGraphicsQueue;
    private Queue presentQueue;
    private Queue computeQueue;
    private Queue transferQueue;
    private bool _supportsMultipleGraphicsQueues;
    private bool _supportsTimelineSemaphores;
    private bool _supportsSynchronization2Feature;
    private ExtDeviceFault? _extDeviceFault;
    private NVDeviceDiagnosticCheckpoints? _nvDeviceDiagnosticCheckpoints;
    private bool _supportsDeviceFault;
    private bool _supportsDeviceFaultVendorBinary;
    private bool _supportsDeviceAddressBindingReport;
    private bool _supportsNvDiagnosticCheckpoints;
    private bool _supportsNvDiagnosticsConfig;

    public Device Device => device;
    internal bool IsLogicalDeviceReady => device.Handle != 0;
    public Queue GraphicsQueue => graphicsQueue;
    public Queue SecondaryGraphicsQueue => secondaryGraphicsQueue;
    public Queue PresentQueue => presentQueue;
    public Queue ComputeQueue => computeQueue;
    public Queue TransferQueue => transferQueue;
    public IReadOnlyList<string> AvailableDeviceExtensions => _availableDeviceExtensions;
    public IReadOnlyList<string> EnabledDeviceExtensions => _enabledDeviceExtensions;
    public bool HasSecondaryGraphicsQueue => _supportsMultipleGraphicsQueues && secondaryGraphicsQueue.Handle != 0;
    public bool SupportsDeviceFault => _supportsDeviceFault &&
        ((_supportsKhrDeviceFault && _vkGetDeviceFaultReportsKHR is not null) ||
         (_supportsExtDeviceFault && _extDeviceFault is not null));
    public bool SupportsDeviceAddressBindingReport => _supportsDeviceAddressBindingReport;
    public bool SupportsNvDiagnosticCheckpoints => _supportsNvDiagnosticCheckpoints && _nvDeviceDiagnosticCheckpoints is not null;
    public bool SupportsNvDiagnosticsConfig => _supportsNvDiagnosticsConfig;

    private void DestroyLogicalDevice()
    {
        ClearPendingDeviceReadyProgramLinks();

        if (!IsLogicalDeviceReady)
            return;

        DestroyGlobalMaterialTextureDescriptorTable();
        DestroyDescriptorHeapBackend();
        DestroyDescriptorUpdateTemplateCache();
        DestroyCachedDescriptorSetLayouts();
        DestroyVulkanPipelineCache();
        DestroyCanonicalImmutableSamplers();
        DestroyRemainingTrackedSamplers();
        ReleaseVulkanDiagnosticStorage();
        MarkDeviceDisposed();
        Api!.DestroyDevice(device, null);
        device = default;
        graphicsQueue = default;
        secondaryGraphicsQueue = default;
        presentQueue = default;
        computeQueue = default;
        transferQueue = default;
    }

    /// <summary>
    /// Checks if an optional device extension is supported by the physical device.
    /// </summary>
    private bool IsDeviceExtensionSupported(string extensionName)
    {
        if (_availableDeviceExtensions.Length == 0)
            _availableDeviceExtensions = EnumerateAvailableDeviceExtensions();

        return _availableDeviceExtensions.Contains(extensionName, StringComparer.Ordinal);
    }

    private string[] EnumerateAvailableDeviceExtensions()
    {
        uint extensionCount = 0;
        Api!.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, ref extensionCount, null);

        if (extensionCount == 0)
            return Array.Empty<string>();

        var availableExtensions = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
        {
            Api!.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, ref extensionCount, availableExtensionsPtr);
        }

        return [.. availableExtensions
            .Select(static ext => SilkMarshal.PtrToString((nint)ext.ExtensionName) ?? string.Empty)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)];
    }

    private unsafe void QueryDescriptorIndexingCapabilities()
    {
        _supportsRuntimeDescriptorArray = false;
        _supportsDescriptorBindingPartiallyBound = false;
        _supportsDescriptorBindingUpdateAfterBind = false;
        _supportsDescriptorBindingStorageImageUpdateAfterBind = false;

        PhysicalDeviceDescriptorIndexingFeatures descriptorIndexingFeatures = new()
        {
            SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &descriptorIndexingFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);

        _supportsRuntimeDescriptorArray = descriptorIndexingFeatures.RuntimeDescriptorArray;
        _supportsDescriptorBindingPartiallyBound = descriptorIndexingFeatures.DescriptorBindingPartiallyBound;
        _supportsDescriptorBindingStorageImageUpdateAfterBind = descriptorIndexingFeatures.DescriptorBindingStorageImageUpdateAfterBind;
        _supportsDescriptorBindingUpdateAfterBind = descriptorIndexingFeatures.DescriptorBindingSampledImageUpdateAfterBind ||
            descriptorIndexingFeatures.DescriptorBindingStorageImageUpdateAfterBind ||
            descriptorIndexingFeatures.DescriptorBindingStorageBufferUpdateAfterBind ||
            descriptorIndexingFeatures.DescriptorBindingUniformBufferUpdateAfterBind;
    }

    private unsafe void QuerySynchronization2Capabilities()
    {
        _supportsSynchronization2Feature = false;

        PhysicalDeviceSynchronization2Features synchronization2Features = new()
        {
            SType = StructureType.PhysicalDeviceSynchronization2Features,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &synchronization2Features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        _supportsSynchronization2Feature = synchronization2Features.Synchronization2;
    }

    private unsafe void QueryDepthClipControlCapabilities(bool extensionEnabled, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionEnabled)
            return;

        PhysicalDeviceDepthClipControlFeaturesEXTNative depthClipControlFeatures = new()
        {
            SType = VulkanDepthClipControlExt.PhysicalDeviceFeaturesSType,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &depthClipControlFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = depthClipControlFeatures.DepthClipControl;
    }

    private unsafe void QueryNvMemoryDecompressionCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out MemoryDecompressionMethodFlagsNV decompressionMethods,
        out ulong maxDecompressionIndirectCount)
    {
        featureSupported = false;
        decompressionMethods = 0;
        maxDecompressionIndirectCount = 0;

        if (!extensionEnabled)
            return;

        PhysicalDeviceMemoryDecompressionFeaturesNV memoryDecompressionFeatures = new()
        {
            SType = StructureType.PhysicalDeviceMemoryDecompressionFeaturesNV,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &memoryDecompressionFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = memoryDecompressionFeatures.MemoryDecompression;

        PhysicalDeviceMemoryDecompressionPropertiesNV memoryDecompressionProperties = new()
        {
            SType = StructureType.PhysicalDeviceMemoryDecompressionPropertiesNV,
            PNext = null,
        };

        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &memoryDecompressionProperties,
        };

        Api!.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        decompressionMethods = (MemoryDecompressionMethodFlagsNV)memoryDecompressionProperties.DecompressionMethods;
        maxDecompressionIndirectCount = memoryDecompressionProperties.MaxDecompressionIndirectCount;
    }

    private unsafe void QueryNvCopyMemoryIndirectCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out ulong supportedQueues)
    {
        featureSupported = false;
        supportedQueues = 0;

        if (!extensionEnabled)
            return;

        PhysicalDeviceCopyMemoryIndirectFeaturesNV copyMemoryIndirectFeatures = new()
        {
            SType = StructureType.PhysicalDeviceCopyMemoryIndirectFeaturesNV,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &copyMemoryIndirectFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = copyMemoryIndirectFeatures.IndirectCopy;

        PhysicalDeviceCopyMemoryIndirectPropertiesNV copyMemoryIndirectProperties = new()
        {
            SType = StructureType.PhysicalDeviceCopyMemoryIndirectPropertiesNV,
            PNext = null,
        };

        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &copyMemoryIndirectProperties,
        };

        Api!.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        supportedQueues = (ulong)copyMemoryIndirectProperties.SupportedQueues;
    }

    private unsafe void QueryBufferDeviceAddressCapabilities(out bool featureSupported)
    {
        featureSupported = false;

        PhysicalDeviceBufferDeviceAddressFeatures bufferDeviceAddressFeatures = new()
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &bufferDeviceAddressFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = bufferDeviceAddressFeatures.BufferDeviceAddress;
    }

    private static bool IsVulkanApiVersionAtLeast(uint apiVersion, uint major, uint minor)
    {
        if (major == 1u && minor == 4u)
            return apiVersion >= VulkanApiVersion14;

        uint actualMajor = apiVersion >> 22;
        uint actualMinor = (apiVersion >> 12) & 0x3FFu;
        return actualMajor > major || actualMajor == major && actualMinor >= minor;
    }

    private static string FormatVulkanApiVersion(uint apiVersion)
    {
        uint major = apiVersion >> 22;
        uint minor = (apiVersion >> 12) & 0x3FFu;
        uint patch = apiVersion & 0xFFFu;
        return $"{major}.{minor}.{patch}";
    }

    private unsafe void QueryMaintenance4Capabilities(
        bool extensionEnabled,
        out bool featureSupported)
    {
        featureSupported = false;

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        bool promotedToCore = properties.ApiVersion >= Vk.Version13;

        PhysicalDeviceMaintenance4Features maintenance4Features = new()
        {
            SType = StructureType.PhysicalDeviceMaintenance4Features,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &maintenance4Features,
        };

        Api.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);

        featureSupported = maintenance4Features.Maintenance4 && (promotedToCore || extensionEnabled);
    }

    private unsafe void QueryDynamicRenderingCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out bool promotedToCore)
    {
        featureSupported = false;
        promotedToCore = false;

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = properties.ApiVersion >= Vk.Version13;

        PhysicalDeviceDynamicRenderingFeatures dynamicRenderingFeatures = new()
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &dynamicRenderingFeatures,
        };

        Api.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);

        featureSupported = dynamicRenderingFeatures.DynamicRendering && (promotedToCore || extensionEnabled);
    }

    private unsafe void QueryDynamicRenderingLocalReadCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out bool promotedToCore,
        out bool depthStencilAttachmentsSupported,
        out bool multisampledAttachmentsSupported)
    {
        featureSupported = false;
        promotedToCore = false;
        depthStencilAttachmentsSupported = false;
        multisampledAttachmentsSupported = false;

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 4u);
        if (!promotedToCore && !extensionEnabled)
            return;

        if (promotedToCore)
        {
            PhysicalDeviceDynamicRenderingLocalReadFeatures localReadFeatures = new()
            {
                SType = StructureType.PhysicalDeviceDynamicRenderingLocalReadFeatures,
                PNext = null,
            };

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &localReadFeatures,
            };

            Api.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
            featureSupported = localReadFeatures.DynamicRenderingLocalRead;

            PhysicalDeviceVulkan14Properties vulkan14Properties = new()
            {
                SType = StructureType.PhysicalDeviceVulkan14Properties,
                PNext = null,
            };

            PhysicalDeviceProperties2 properties2 = new()
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &vulkan14Properties,
            };

            Api.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
            _vulkan14Properties = vulkan14Properties;
            depthStencilAttachmentsSupported = vulkan14Properties.DynamicRenderingLocalReadDepthStencilAttachments;
            multisampledAttachmentsSupported = vulkan14Properties.DynamicRenderingLocalReadMultisampledAttachments;
            return;
        }

        PhysicalDeviceDynamicRenderingLocalReadFeaturesKHR localReadFeaturesKhr = new()
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingLocalReadFeaturesKhr,
            PNext = null,
        };

        PhysicalDeviceFeatures2 khrFeatures2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &localReadFeaturesKhr,
        };

        Api.GetPhysicalDeviceFeatures2(_physicalDevice, &khrFeatures2);
        featureSupported = localReadFeaturesKhr.DynamicRenderingLocalRead;
    }

    private unsafe void QueryMaintenance5Capabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out bool promotedToCore)
    {
        featureSupported = false;
        promotedToCore = false;

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 4u);
        if (!promotedToCore && !extensionEnabled)
            return;

        if (promotedToCore)
        {
            PhysicalDeviceMaintenance5Features maintenance5Features = new()
            {
                SType = StructureType.PhysicalDeviceMaintenance5Features,
                PNext = null,
            };

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &maintenance5Features,
            };

            Api.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
            featureSupported = maintenance5Features.Maintenance5;
            return;
        }

        PhysicalDeviceMaintenance5FeaturesKHR maintenance5FeaturesKhr = new()
        {
            SType = StructureType.PhysicalDeviceMaintenance5FeaturesKhr,
            PNext = null,
        };

        PhysicalDeviceFeatures2 khrFeatures2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &maintenance5FeaturesKhr,
        };

        Api.GetPhysicalDeviceFeatures2(_physicalDevice, &khrFeatures2);
        featureSupported = maintenance5FeaturesKhr.Maintenance5;
    }

    private unsafe void QueryShaderObjectCapabilities(
        bool extensionAvailable,
        out bool featureSupported,
        out PhysicalDeviceShaderObjectPropertiesEXT properties)
    {
        featureSupported = false;
        properties = default;
        if (!extensionAvailable)
            return;

        PhysicalDeviceShaderObjectFeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceShaderObjectFeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = features.ShaderObject;

        PhysicalDeviceShaderObjectPropertiesEXT queriedProperties = new()
        {
            SType = StructureType.PhysicalDeviceShaderObjectPropertiesExt,
            PNext = null,
        };

        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &queriedProperties,
        };

        Api.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        properties = queriedProperties;
    }

    private unsafe void QueryMemoryPriorityCapabilities(bool extensionAvailable, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionAvailable)
            return;

        PhysicalDeviceMemoryPriorityFeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceMemoryPriorityFeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = features.MemoryPriority;
    }

    private unsafe void QueryAccelerationStructureCapabilities(bool extensionAvailable, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionAvailable)
            return;

        PhysicalDeviceAccelerationStructureFeaturesKHR features = new()
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = features.AccelerationStructure;
    }

    private unsafe void QueryRayTracingPipelineCapabilities(bool extensionAvailable, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionAvailable)
            return;

        PhysicalDeviceRayTracingPipelineFeaturesKHR features = new()
        {
            SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = features.RayTracingPipeline;
    }

    private unsafe void QueryRayQueryCapabilities(bool extensionAvailable, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionAvailable)
            return;

        PhysicalDeviceRayQueryFeaturesKHR features = new()
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = features.RayQuery;
    }

    private unsafe void QueryDeviceGeneratedCommandsCapabilities(bool extensionAvailable, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionAvailable)
            return;

        PhysicalDeviceDeviceGeneratedCommandsFeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceDeviceGeneratedCommandsFeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = features.DeviceGeneratedCommands;
    }

    private unsafe void QueryDeviceFaultCapabilities(
        bool extensionEnabled,
        out bool deviceFaultSupported,
        out bool vendorBinarySupported)
    {
        deviceFaultSupported = false;
        vendorBinarySupported = false;
        if (!extensionEnabled)
            return;

        PhysicalDeviceFaultFeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceFaultFeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        deviceFaultSupported = features.DeviceFault;
        vendorBinarySupported = features.DeviceFaultVendorBinary;
    }

    private unsafe void QueryDeviceAddressBindingReportCapabilities(
        bool extensionEnabled,
        out bool reportAddressBindingSupported)
    {
        reportAddressBindingSupported = false;
        if (!extensionEnabled)
            return;

        PhysicalDeviceAddressBindingReportFeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceAddressBindingReportFeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        reportAddressBindingSupported = features.ReportAddressBinding;
    }

    private unsafe void QueryNvDiagnosticsConfigCapabilities(
        bool extensionEnabled,
        out bool diagnosticsConfigSupported)
    {
        diagnosticsConfigSupported = false;
        if (!extensionEnabled)
            return;

        PhysicalDeviceDiagnosticsConfigFeaturesNV features = new()
        {
            SType = StructureType.PhysicalDeviceDiagnosticsConfigFeaturesNV,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        diagnosticsConfigSupported = features.DiagnosticsConfig;
    }

    private unsafe void QueryShaderDrawParametersCapabilities(out bool featureSupported)
    {
        featureSupported = false;

        PhysicalDeviceVulkan11Features vulkan11Features = new()
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vulkan11Features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = vulkan11Features.ShaderDrawParameters;
    }

    private unsafe void QueryVulkan12Capabilities(
        out PhysicalDeviceVulkan12Features vulkan12Features,
        out bool promotedToCore)
    {
        vulkan12Features = new()
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = null,
        };

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = properties.ApiVersion >= Vk.Version12;
        if (!promotedToCore)
            return;

        PhysicalDeviceVulkan12Features queriedFeatures = new()
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &queriedFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        vulkan12Features = queriedFeatures;
    }

    private unsafe void QueryMultiviewCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out bool promotedToCore)
    {
        featureSupported = false;
        promotedToCore = false;

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = properties.ApiVersion >= Vk.Version11;

        PhysicalDeviceVulkan11Features vulkan11Features = new()
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vulkan11Features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = vulkan11Features.Multiview && (promotedToCore || extensionEnabled);
    }

    private unsafe void QueryIndexTypeUint8Capabilities(out bool featureSupported)
    {
        featureSupported = false;

        PhysicalDeviceIndexTypeUint8FeaturesEXT indexTypeUint8Features = new()
        {
            SType = StructureType.PhysicalDeviceIndexTypeUint8FeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &indexTypeUint8Features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = indexTypeUint8Features.IndexTypeUint8;
    }

    private unsafe void QueryTimelineSemaphoreCapabilities(out bool featureSupported)
    {
        featureSupported = false;

        PhysicalDeviceTimelineSemaphoreFeatures timelineFeatures = new()
        {
            SType = StructureType.PhysicalDeviceTimelineSemaphoreFeatures,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &timelineFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = timelineFeatures.TimelineSemaphore;
    }

    private unsafe void QueryMeshShaderCapabilities(
        bool extensionEnabled,
        out bool taskShaderSupported,
        out bool meshShaderSupported)
    {
        taskShaderSupported = false;
        meshShaderSupported = false;

        if (!extensionEnabled)
            return;

        PhysicalDeviceMeshShaderFeaturesEXT meshShaderFeatures = new()
        {
            SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &meshShaderFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        taskShaderSupported = meshShaderFeatures.TaskShader;
        meshShaderSupported = meshShaderFeatures.MeshShader;
    }

    private unsafe void QueryGraphicsPipelineLibraryCapabilities(
        bool extensionEnabled,
        out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionEnabled)
            return;

        PhysicalDeviceGraphicsPipelineLibraryFeaturesEXT graphicsPipelineLibraryFeatures = new()
        {
            SType = StructureType.PhysicalDeviceGraphicsPipelineLibraryFeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &graphicsPipelineLibraryFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = graphicsPipelineLibraryFeatures.GraphicsPipelineLibrary;
    }

    private unsafe void QueryTransformFeedbackCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out bool geometryStreamsSupported,
        out PhysicalDeviceTransformFeedbackPropertiesEXT properties)
    {
        featureSupported = false;
        geometryStreamsSupported = false;
        properties = default;

        if (!extensionEnabled)
            return;

        PhysicalDeviceTransformFeedbackFeaturesEXT transformFeedbackFeatures = new()
        {
            SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &transformFeedbackFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = transformFeedbackFeatures.TransformFeedback;
        geometryStreamsSupported = transformFeedbackFeatures.GeometryStreams;

        PhysicalDeviceTransformFeedbackPropertiesEXT queriedProperties = new()
        {
            SType = StructureType.PhysicalDeviceTransformFeedbackPropertiesExt,
            PNext = null,
        };

        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &queriedProperties,
        };

        Api.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        properties = queriedProperties;
    }

    private unsafe void QueryFragmentShadingRateCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out bool pipelineFragmentShadingRate,
        out bool primitiveFragmentShadingRate,
        out bool attachmentFragmentShadingRate,
        out PhysicalDeviceFragmentShadingRatePropertiesKHR properties)
    {
        featureSupported = false;
        pipelineFragmentShadingRate = false;
        primitiveFragmentShadingRate = false;
        attachmentFragmentShadingRate = false;
        properties = default;

        if (!extensionEnabled)
            return;

        PhysicalDeviceFragmentShadingRateFeaturesKHR features = new()
        {
            SType = StructureType.PhysicalDeviceFragmentShadingRateFeaturesKhr,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        pipelineFragmentShadingRate = features.PipelineFragmentShadingRate;
        primitiveFragmentShadingRate = features.PrimitiveFragmentShadingRate;
        attachmentFragmentShadingRate = features.AttachmentFragmentShadingRate;
        featureSupported = pipelineFragmentShadingRate || primitiveFragmentShadingRate || attachmentFragmentShadingRate;

        PhysicalDeviceFragmentShadingRatePropertiesKHR queriedProperties = new()
        {
            SType = StructureType.PhysicalDeviceFragmentShadingRatePropertiesKhr,
        };
        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &queriedProperties,
        };
        Api.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        properties = queriedProperties;
    }

    private unsafe void QueryFragmentDensityMapCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out bool dynamicSupported,
        out bool nonSubsampledImagesSupported)
    {
        featureSupported = false;
        dynamicSupported = false;
        nonSubsampledImagesSupported = false;

        if (!extensionEnabled)
            return;

        PhysicalDeviceFragmentDensityMapFeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceFragmentDensityMapFeaturesExt,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
        featureSupported = features.FragmentDensityMap;
        dynamicSupported = features.FragmentDensityMapDynamic;
        nonSubsampledImagesSupported = features.FragmentDensityMapNonSubsampledImages;
    }

    /// <summary>
    /// Creates a logical device interface to the physical device with specific 
    /// queue families and extensions.
    /// </summary>
    /// <remarks>
    /// The logical device is the primary interface for an application to the GPU.
    /// This method:
    /// 1. Identifies required queue families (graphics and presentation)
    /// 2. Sets up queue creation information
    /// 3. Configures device features
    /// 4. Enables required device extensions
    /// 5. Enables validation layers if needed
    /// 6. Creates the device and obtains queue handles
    /// </remarks>
    private void CreateLogicalDevice()
    {
        // Get queue family indices required for rendering and presentation
        var indices = FamilyQueueIndices;

        // Query queue family capabilities so we can request multiple graphics queues when available.
        uint queueFamilyCount = 0;
        Api!.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, null);
        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
            Api!.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, queueFamiliesPtr);

        uint graphicsFamilyQueueCount = queueFamilies[indices.GraphicsFamilyIndex!.Value].QueueCount;
        uint engineGraphicsQueueCount = Math.Min(2u, graphicsFamilyQueueCount);
        _supportsMultipleGraphicsQueues = engineGraphicsQueueCount > 1;

        uint graphicsFamily = indices.GraphicsFamilyIndex.Value;
        uint computeFamily = indices.ComputeFamilyIndex ?? graphicsFamily;
        uint transferFamily = indices.TransferFamilyIndex ?? computeFamily;
        uint presentFamily = indices.PresentFamilyIndex!.Value;
        Dictionary<uint, uint> requestedQueueCounts = [];

        static void RequireEngineQueues(Dictionary<uint, uint> counts, uint family, uint count)
        {
            if (!counts.TryGetValue(family, out uint existing) || existing < count)
                counts[family] = count;
        }

        RequireEngineQueues(requestedQueueCounts, graphicsFamily, engineGraphicsQueueCount);
        RequireEngineQueues(requestedQueueCounts, presentFamily, 1);
        RequireEngineQueues(requestedQueueCounts, computeFamily, 1);
        RequireEngineQueues(requestedQueueCounts, transferFamily, 1);

        _streamlineGraphicsQueueFamily = graphicsFamily;
        _streamlineComputeQueueFamily = computeFamily;
        _streamlineOpticalFlowQueueFamily = 0;
        _streamlineGraphicsQueueIndex = AppendRequiredQueues(
            requestedQueueCounts,
            queueFamilies,
            graphicsFamily,
            _streamlineQueueRequirements.GraphicsQueues,
            "graphics");
        _streamlineComputeQueueIndex = AppendRequiredQueues(
            requestedQueueCounts,
            queueFamilies,
            computeFamily,
            _streamlineQueueRequirements.ComputeQueues,
            "compute");

        if (_streamlineQueueRequirements.OpticalFlowQueues > 0)
        {
            _streamlineOpticalFlowQueueFamily = FindOpticalFlowQueueFamily(queueFamilies);
            _streamlineOpticalFlowQueueIndex = AppendRequiredQueues(
                requestedQueueCounts,
                queueFamilies,
                _streamlineOpticalFlowQueueFamily,
                _streamlineQueueRequirements.OpticalFlowQueues,
                "optical-flow");
        }
        else
        {
            _streamlineOpticalFlowQueueIndex = 0;
        }

        uint[] uniqueQueueFamilies = [.. requestedQueueCounts.Keys];

        // Allocate memory for queue create infos
        using var mem = GlobalMemory.Allocate(uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo));
        var queueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());

        // Configure queue priorities (1.0 = highest priority)
        int maxRequestedQueueCount = checked((int)requestedQueueCounts.Values.Max());
        float* queuePriorities = stackalloc float[maxRequestedQueueCount];
        for (int queueIndex = 0; queueIndex < maxRequestedQueueCount; queueIndex++)
            queuePriorities[queueIndex] = 1.0f;

        // Set up creation info for each queue family
        for (int i = 0; i < uniqueQueueFamilies.Length; i++)
        {
            uint queueFamilyIndex = uniqueQueueFamilies[i];

            queueCreateInfos[i] = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                QueueCount = requestedQueueCounts[queueFamilyIndex],
                PQueuePriorities = queuePriorities
            };
        }

        // Specify device features to enable (none specifically enabled here)
        PhysicalDeviceFeatures supportedFeatures = new();
        Api!.GetPhysicalDeviceFeatures(_physicalDevice, out supportedFeatures);

        PhysicalDeviceFeatures deviceFeatures = new();
        if (supportedFeatures.RobustBufferAccess)
            deviceFeatures.RobustBufferAccess = Vk.True;

        if (supportedFeatures.SamplerAnisotropy)
        {
            deviceFeatures.SamplerAnisotropy = Vk.True;
            _supportsAnisotropy = true;
        }

        if (supportedFeatures.FragmentStoresAndAtomics)
        {
            deviceFeatures.FragmentStoresAndAtomics = Vk.True;
            _supportsFragmentStoresAndAtomics = true;
        }

        if (supportedFeatures.VertexPipelineStoresAndAtomics)
        {
            deviceFeatures.VertexPipelineStoresAndAtomics = Vk.True;
            _supportsVertexPipelineStoresAndAtomics = true;
        }

        if (supportedFeatures.GeometryShader)
        {
            deviceFeatures.GeometryShader = Vk.True;
            _supportsGeometryShader = true;
        }

        bool enableMultiViewport = supportedFeatures.MultiViewport;
        if (enableMultiViewport)
            deviceFeatures.MultiViewport = Vk.True;

        if (supportedFeatures.SampleRateShading)
            deviceFeatures.SampleRateShading = Vk.True;

        if (supportedFeatures.IndependentBlend)
            deviceFeatures.IndependentBlend = Vk.True;

        if (supportedFeatures.MultiDrawIndirect)
        {
            deviceFeatures.MultiDrawIndirect = Vk.True;
            _supportsMultiDrawIndirect = true;
        }

        if (supportedFeatures.DrawIndirectFirstInstance)
        {
            deviceFeatures.DrawIndirectFirstInstance = Vk.True;
            _supportsDrawIndirectFirstInstance = true;
        }

        QueryVulkan12Capabilities(
            out PhysicalDeviceVulkan12Features supportedVulkan12Features,
            out bool vulkan12PromotedToCore);

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties physicalDeviceProperties);
        bool vulkan13PromotedToCore = IsVulkanApiVersionAtLeast(physicalDeviceProperties.ApiVersion, 1u, 3u);
        bool vulkan14PromotedToCore = IsVulkanApiVersionAtLeast(physicalDeviceProperties.ApiVersion, 1u, 4u);

        _availableDeviceExtensions = EnumerateAvailableDeviceExtensions();
        var availableExtensionSet = new HashSet<string>(_availableDeviceExtensions, StringComparer.Ordinal);

        // Build the list of extensions to enable (required + supported optional)
        var extensionsToEnable = new List<string>(_requiredDeviceExtensions);
        foreach (string requiredStreamlineExtension in _streamlineRequiredDeviceExtensions)
        {
            if (!availableExtensionSet.Contains(requiredStreamlineExtension))
            {
                throw new NotSupportedException(
                    $"Streamline requires Vulkan device extension '{requiredStreamlineExtension}', but the selected physical device does not expose it.");
            }

            if (!extensionsToEnable.Contains(requiredStreamlineExtension, StringComparer.Ordinal))
                extensionsToEnable.Add(requiredStreamlineExtension);
        }

        var openXrRequirements = OpenXRAPI.GetRequestedVulkanRuntimeRequirements();
        foreach (string requiredOpenXrExtension in openXrRequirements.DeviceExtensions)
        {
            if (string.IsNullOrWhiteSpace(requiredOpenXrExtension))
                continue;

            if (!availableExtensionSet.Contains(requiredOpenXrExtension))
            {
                throw new NotSupportedException(
                    $"The active OpenXR runtime requires Vulkan device extension '{requiredOpenXrExtension}', " +
                    "but the selected Vulkan physical device does not expose it.");
            }

            if (!extensionsToEnable.Contains(requiredOpenXrExtension, StringComparer.Ordinal))
            {
                extensionsToEnable.Add(requiredOpenXrExtension);
                Debug.Vulkan($"[OpenXR] Enabling required Vulkan device extension: {requiredOpenXrExtension}");
            }
        }

        void AddDiagnosticDeviceExtensionIfRequested(string extensionName, bool requested)
        {
            if (!requested)
                return;

            if (availableExtensionSet.Contains(extensionName))
            {
                if (!extensionsToEnable.Contains(extensionName, StringComparer.Ordinal))
                {
                    extensionsToEnable.Add(extensionName);
                    Debug.Vulkan("[VulkanDiag] Enabling requested diagnostic device extension: {0}", extensionName);
                }
            }
            else
            {
                Debug.VulkanWarning("[VulkanDiag] Requested diagnostic device extension is unsupported: {0}", extensionName);
            }
        }

        if (_diagnosticOptions.RequestDeviceFault && availableExtensionSet.Contains(KhrDeviceFaultExtensionName))
        {
            Debug.Vulkan(
                "[VulkanDiag] {0} is exposed; preferring local KHR device-fault shim with {1} compatibility fallback when available.",
                KhrDeviceFaultExtensionName,
                ExtDeviceFaultExtensionName);
        }

        AddDiagnosticDeviceExtensionIfRequested(KhrDeviceFaultExtensionName, _diagnosticOptions.RequestDeviceFault);
        AddDiagnosticDeviceExtensionIfRequested(ExtDeviceFaultExtensionName, _diagnosticOptions.RequestDeviceFault);
        AddDiagnosticDeviceExtensionIfRequested(ExtDeviceAddressBindingReportExtensionName, _diagnosticOptions.RequestDeviceAddressBindingReport);
        AddDiagnosticDeviceExtensionIfRequested(NvDeviceDiagnosticCheckpointsExtensionName, _diagnosticOptions.RequestNvDiagnosticCheckpoints);
        AddDiagnosticDeviceExtensionIfRequested(NvDeviceDiagnosticsConfigExtensionName, _diagnosticOptions.RequestNvDiagnosticsConfig);

        foreach (var optionalExt in _optionalDeviceExtensions)
        {
            if (optionalExt == "VK_EXT_graphics_pipeline_library" &&
                !availableExtensionSet.Contains("VK_KHR_pipeline_library"))
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because required dependency VK_KHR_pipeline_library is unavailable.",
                    optionalExt);
                continue;
            }

            if (optionalExt == "VK_KHR_dynamic_rendering_local_read" &&
                !vulkan13PromotedToCore &&
                !availableExtensionSet.Contains("VK_KHR_dynamic_rendering"))
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because dynamic rendering is unavailable.",
                    optionalExt);
                continue;
            }

            if (vulkan12PromotedToCore && optionalExt == "VK_KHR_draw_indirect_count" && !supportedVulkan12Features.DrawIndirectCount)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because Vulkan 1.2 drawIndirectCount feature is unavailable.",
                    optionalExt);
                continue;
            }

            if (vulkan12PromotedToCore && optionalExt == "VK_EXT_descriptor_indexing" && !supportedVulkan12Features.DescriptorIndexing)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because Vulkan 1.2 descriptorIndexing feature is unavailable.",
                    optionalExt);
                continue;
            }

            if (vulkan12PromotedToCore && optionalExt == "VK_KHR_buffer_device_address" && !supportedVulkan12Features.BufferDeviceAddress)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because Vulkan 1.2 bufferDeviceAddress feature is unavailable.",
                    optionalExt);
                continue;
            }

            if (vulkan12PromotedToCore && optionalExt == "VK_KHR_timeline_semaphore" && !supportedVulkan12Features.TimelineSemaphore)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Optional extension {0} skipped because Vulkan 1.2 timelineSemaphore feature is unavailable.",
                    optionalExt);
                continue;
            }

            if (availableExtensionSet.Contains(optionalExt))
            {
                extensionsToEnable.Add(optionalExt);
                Debug.Vulkan($"[Vulkan] Enabling optional extension: {optionalExt}");
            }
            else
            {
                Debug.Vulkan($"[Vulkan] Optional extension not supported: {optionalExt}");
            }
        }

        var extensionsArray = extensionsToEnable.ToArray();
        _enabledDeviceExtensions = [.. extensionsArray
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)];

        ValidateObsHookDeviceCompatibility(availableExtensionSet, extensionsArray);

        bool drawIndirectCountExtensionEnabled = extensionsArray.Contains("VK_KHR_draw_indirect_count");
        bool descriptorIndexingExtensionEnabled = extensionsArray.Contains("VK_EXT_descriptor_indexing");
        bool descriptorIndexingRequestedByProfile = VulkanFeatureProfile.EnableDescriptorIndexing;
        QueryDescriptorIndexingCapabilities();
        bool synchronization2ExtensionEnabled = extensionsArray.Contains("VK_KHR_synchronization2");
        QuerySynchronization2Capabilities();

        bool descriptorIndexingCapabilityReady =
            _supportsRuntimeDescriptorArray &&
            _supportsDescriptorBindingPartiallyBound &&
            _supportsDescriptorBindingUpdateAfterBind;

        bool enableDescriptorIndexing = descriptorIndexingExtensionEnabled &&
            descriptorIndexingRequestedByProfile &&
            descriptorIndexingCapabilityReady;

        bool nvMemoryDecompressionExtensionEnabled = extensionsArray.Contains("VK_NV_memory_decompression");
        bool nvMemoryDecompressionRequestedByProfile = VulkanFeatureProfile.EnableRtxIoVulkanDecompression;

        QueryNvMemoryDecompressionCapabilities(
            nvMemoryDecompressionExtensionEnabled,
            out bool nvMemoryDecompressionFeatureSupported,
            out MemoryDecompressionMethodFlagsNV nvMemoryDecompressionMethods,
            out ulong nvMaxDecompressionIndirectCount);

        bool enableNvMemoryDecompression =
            nvMemoryDecompressionExtensionEnabled &&
            nvMemoryDecompressionRequestedByProfile &&
            nvMemoryDecompressionFeatureSupported;

        bool nvCopyMemoryIndirectExtensionEnabled = extensionsArray.Contains("VK_NV_copy_memory_indirect");
        bool nvCopyMemoryIndirectRequestedByProfile = VulkanFeatureProfile.EnableRtxIoVulkanCopyMemoryIndirect;

        QueryNvCopyMemoryIndirectCapabilities(
            nvCopyMemoryIndirectExtensionEnabled,
            out bool nvCopyMemoryIndirectFeatureSupported,
            out ulong nvCopyMemoryIndirectSupportedQueues);

        bool enableNvCopyMemoryIndirect =
            nvCopyMemoryIndirectExtensionEnabled &&
            nvCopyMemoryIndirectRequestedByProfile &&
            nvCopyMemoryIndirectFeatureSupported;

        bool bufferDeviceAddressExtensionEnabled = extensionsArray.Contains("VK_KHR_buffer_device_address");
        QueryBufferDeviceAddressCapabilities(out bool bufferDeviceAddressFeatureSupported);
        bool bufferDeviceAddressRequestedBySceneDatabase =
            VulkanFeatureProfile.ActiveGeometryFetchMode == EVulkanGeometryFetchMode.BufferDeviceAddressPrototype ||
            VulkanFeatureProfile.EnableBindlessMaterialTable;
        bool enableBufferDeviceAddress =
            bufferDeviceAddressFeatureSupported &&
            (enableNvCopyMemoryIndirect || bufferDeviceAddressRequestedBySceneDatabase || bufferDeviceAddressExtensionEnabled);

        bool dynamicRenderingExtensionEnabled = extensionsArray.Contains("VK_KHR_dynamic_rendering");
        QueryDynamicRenderingCapabilities(
            dynamicRenderingExtensionEnabled,
            out bool dynamicRenderingFeatureSupported,
            out bool dynamicRenderingPromotedToCore);
        bool enableDynamicRenderingFeature = dynamicRenderingFeatureSupported;

        bool dynamicRenderingLocalReadExtensionEnabled = extensionsArray.Contains("VK_KHR_dynamic_rendering_local_read");
        QueryDynamicRenderingLocalReadCapabilities(
            dynamicRenderingLocalReadExtensionEnabled,
            out bool dynamicRenderingLocalReadFeatureSupported,
            out bool dynamicRenderingLocalReadPromotedToCore,
            out bool dynamicRenderingLocalReadDepthStencilSupported,
            out bool dynamicRenderingLocalReadMultisampledSupported);
        bool enableDynamicRenderingLocalReadFeature =
            enableDynamicRenderingFeature &&
            dynamicRenderingLocalReadFeatureSupported;

        bool shaderDrawParametersExtensionEnabled = extensionsArray.Contains("VK_KHR_shader_draw_parameters");
        QueryShaderDrawParametersCapabilities(out bool shaderDrawParametersFeatureSupported);
        bool enableShaderDrawParametersFeature = shaderDrawParametersFeatureSupported;

        bool shaderOutputViewportIndexFeatureSupported =
            vulkan12PromotedToCore && supportedVulkan12Features.ShaderOutputViewportIndex;
        bool shaderOutputLayerFeatureSupported =
            vulkan12PromotedToCore && supportedVulkan12Features.ShaderOutputLayer;
        bool shaderViewportLayerPromotedToCore = vulkan12PromotedToCore;
        bool enableShaderOutputViewportIndexFeature = shaderOutputViewportIndexFeatureSupported;
        bool enableShaderOutputLayerFeature = shaderOutputLayerFeatureSupported;
        bool enableDrawIndirectCountFeature =
            vulkan12PromotedToCore &&
            supportedVulkan12Features.DrawIndirectCount;

        // Host query reset (core 1.2). Occlusion query pools must be reset outside a render
        // pass instance; CPU occlusion QueryOps are recorded while the target render pass is
        // active, so the reset has to happen on the host instead of the command buffer.
        bool hostQueryResetFeatureSupported =
            vulkan12PromotedToCore && supportedVulkan12Features.HostQueryReset;
        bool enableHostQueryResetFeature = hostQueryResetFeatureSupported;

        bool multiviewExtensionEnabled = extensionsArray.Contains("VK_KHR_multiview");
        QueryMultiviewCapabilities(
            multiviewExtensionEnabled,
            out bool multiviewFeatureSupported,
            out bool multiviewPromotedToCore);
        bool enableMultiviewFeature = multiviewFeatureSupported;

        bool indexTypeUint8ExtensionEnabled =
            extensionsArray.Contains("VK_EXT_index_type_uint8") ||
            extensionsArray.Contains("VK_KHR_index_type_uint8");
        QueryIndexTypeUint8Capabilities(out bool indexTypeUint8FeatureSupported);
        bool enableIndexTypeUint8Feature = indexTypeUint8FeatureSupported;

        bool maintenance4ExtensionEnabled = extensionsArray.Contains("VK_KHR_maintenance4");
        QueryMaintenance4Capabilities(
            maintenance4ExtensionEnabled,
            out bool maintenance4FeatureSupported);
        bool enableMaintenance4Feature = maintenance4FeatureSupported;

        bool maintenance5ExtensionEnabled = extensionsArray.Contains("VK_KHR_maintenance5");
        QueryMaintenance5Capabilities(
            maintenance5ExtensionEnabled,
            out bool maintenance5FeatureSupported,
            out bool maintenance5PromotedToCore);
        bool enableMaintenance5Feature = maintenance5FeatureSupported;

        bool extendedFlagsExtensionAvailable = availableExtensionSet.Contains("VK_KHR_extended_flags");
        bool extendedFlagsExtensionEnabled = extensionsArray.Contains("VK_KHR_extended_flags");
        bool descriptorHeapExtensionAvailable = availableExtensionSet.Contains(VulkanDescriptorHeapExt.ExtensionName);
        bool descriptorHeapExtensionEnabled = extensionsArray.Contains(VulkanDescriptorHeapExt.ExtensionName);
        bool shaderUntypedPointersExtensionAvailable = availableExtensionSet.Contains(VulkanDescriptorHeapExt.ShaderUntypedPointersExtensionName);
        bool descriptorBufferExtensionAvailable = availableExtensionSet.Contains("VK_EXT_descriptor_buffer");
        bool memoryBudgetExtensionAvailable = availableExtensionSet.Contains("VK_EXT_memory_budget");
        bool memoryBudgetExtensionEnabled = extensionsArray.Contains("VK_EXT_memory_budget");
        bool memoryPriorityExtensionAvailable = availableExtensionSet.Contains("VK_EXT_memory_priority");
        QueryMemoryPriorityCapabilities(
            memoryPriorityExtensionAvailable,
            out bool memoryPriorityFeatureSupported);

        bool shaderObjectExtensionAvailable = availableExtensionSet.Contains("VK_EXT_shader_object");
        QueryShaderObjectCapabilities(
            shaderObjectExtensionAvailable,
            out bool shaderObjectFeatureSupported,
            out PhysicalDeviceShaderObjectPropertiesEXT shaderObjectProperties);

        bool accelerationStructureExtensionAvailable = availableExtensionSet.Contains("VK_KHR_acceleration_structure");
        QueryAccelerationStructureCapabilities(
            accelerationStructureExtensionAvailable,
            out bool accelerationStructureFeatureSupported);
        bool rayTracingPipelineExtensionAvailable =
            availableExtensionSet.Contains("VK_KHR_ray_tracing_pipeline") &&
            availableExtensionSet.Contains("VK_KHR_deferred_host_operations");
        QueryRayTracingPipelineCapabilities(
            rayTracingPipelineExtensionAvailable,
            out bool rayTracingPipelineFeatureSupported);
        bool rayQueryExtensionAvailable = availableExtensionSet.Contains("VK_KHR_ray_query");
        QueryRayQueryCapabilities(
            rayQueryExtensionAvailable,
            out bool rayQueryFeatureSupported);
        bool deviceGeneratedCommandsExtensionAvailable = availableExtensionSet.Contains("VK_EXT_device_generated_commands");
        QueryDeviceGeneratedCommandsCapabilities(
            deviceGeneratedCommandsExtensionAvailable,
            out bool deviceGeneratedCommandsFeatureSupported);
        bool khrDeviceFaultExtensionAvailable = availableExtensionSet.Contains(KhrDeviceFaultExtensionName);
        bool extDeviceFaultExtensionAvailable = availableExtensionSet.Contains(ExtDeviceFaultExtensionName);
        bool khrDeviceFaultExtensionEnabled = extensionsArray.Contains(KhrDeviceFaultExtensionName);
        bool extDeviceFaultExtensionEnabled = extensionsArray.Contains(ExtDeviceFaultExtensionName);
        QueryKhrDeviceFaultCapabilities(
            khrDeviceFaultExtensionEnabled,
            out bool khrDeviceFaultFeatureSupported,
            out bool khrDeviceFaultVendorBinaryFeatureSupported,
            out bool khrDeviceFaultReportMaskedFeatureSupported,
            out bool khrDeviceFaultDeviceLostOnMaskedFeatureSupported,
            out uint khrDeviceFaultMaxReportCount);
        QueryDeviceFaultCapabilities(
            extDeviceFaultExtensionEnabled,
            out bool extDeviceFaultFeatureSupported,
            out bool extDeviceFaultVendorBinaryFeatureSupported);
        bool enableKhrDeviceFaultFeature =
            _diagnosticOptions.RequestDeviceFault &&
            khrDeviceFaultExtensionEnabled &&
            khrDeviceFaultFeatureSupported;
        bool enableKhrDeviceFaultVendorBinary =
            enableKhrDeviceFaultFeature &&
            khrDeviceFaultVendorBinaryFeatureSupported;
        bool enableKhrDeviceFaultReportMasked =
            enableKhrDeviceFaultFeature &&
            _diagnosticOptions.Preset == EVulkanDiagnosticPreset.CrashDiagnostics &&
            khrDeviceFaultReportMaskedFeatureSupported;
        bool enableKhrDeviceFaultDeviceLostOnMasked =
            enableKhrDeviceFaultFeature &&
            _diagnosticOptions.RequestDeviceFaultDeviceLostOnMasked &&
            khrDeviceFaultDeviceLostOnMaskedFeatureSupported;
        bool enableExtDeviceFaultFeature =
            _diagnosticOptions.RequestDeviceFault &&
            extDeviceFaultExtensionEnabled &&
            extDeviceFaultFeatureSupported;
        bool enableDeviceFaultFeature =
            enableKhrDeviceFaultFeature ||
            enableExtDeviceFaultFeature;

        bool deviceAddressBindingReportExtensionAvailable = availableExtensionSet.Contains(ExtDeviceAddressBindingReportExtensionName);
        bool deviceAddressBindingReportExtensionEnabled = extensionsArray.Contains(ExtDeviceAddressBindingReportExtensionName);
        QueryDeviceAddressBindingReportCapabilities(
            deviceAddressBindingReportExtensionEnabled,
            out bool deviceAddressBindingReportFeatureSupported);
        bool enableDeviceAddressBindingReportFeature =
            _diagnosticOptions.RequestDeviceAddressBindingReport &&
            deviceAddressBindingReportExtensionEnabled &&
            deviceAddressBindingReportFeatureSupported;

        bool nvDiagnosticCheckpointsExtensionAvailable = availableExtensionSet.Contains(NvDeviceDiagnosticCheckpointsExtensionName);
        bool nvDiagnosticCheckpointsExtensionEnabled = extensionsArray.Contains(NvDeviceDiagnosticCheckpointsExtensionName);
        bool enableNvDiagnosticCheckpoints = _diagnosticOptions.RequestNvDiagnosticCheckpoints && nvDiagnosticCheckpointsExtensionEnabled;

        bool nvDiagnosticsConfigExtensionAvailable = availableExtensionSet.Contains(NvDeviceDiagnosticsConfigExtensionName);
        bool nvDiagnosticsConfigExtensionEnabled = extensionsArray.Contains(NvDeviceDiagnosticsConfigExtensionName);
        QueryNvDiagnosticsConfigCapabilities(
            nvDiagnosticsConfigExtensionEnabled,
            out bool nvDiagnosticsConfigFeatureSupported);
        bool enableNvDiagnosticsConfigFeature =
            _diagnosticOptions.RequestNvDiagnosticsConfig &&
            nvDiagnosticsConfigExtensionEnabled &&
            nvDiagnosticsConfigFeatureSupported;
        bool descriptorHeapDependenciesReady =
            descriptorHeapExtensionAvailable &&
            (vulkan14PromotedToCore ||
             ((maintenance5FeatureSupported || extendedFlagsExtensionAvailable) &&
              (bufferDeviceAddressFeatureSupported || vulkan12PromotedToCore))) &&
            shaderUntypedPointersExtensionAvailable;
        QueryDescriptorHeapCapabilities(
            descriptorHeapExtensionAvailable,
            shaderUntypedPointersExtensionAvailable,
            out bool descriptorHeapFeatureSupported,
            out bool descriptorHeapCaptureReplaySupported,
            out PhysicalDeviceDescriptorHeapPropertiesEXTNative descriptorHeapProperties);
        bool enableDescriptorHeapFeature =
            descriptorHeapExtensionEnabled &&
            descriptorHeapDependenciesReady &&
            descriptorHeapFeatureSupported;

        QueryTimelineSemaphoreCapabilities(out bool timelineSemaphoreFeatureSupported);
        bool enableTimelineSemaphoreFeature = timelineSemaphoreFeatureSupported;
        bool enableSynchronization2Feature = synchronization2ExtensionEnabled && _supportsSynchronization2Feature;

        bool depthClipControlExtensionEnabled = extensionsArray.Contains(VulkanDepthClipControlExt.ExtensionName);
        QueryDepthClipControlCapabilities(
            depthClipControlExtensionEnabled,
            out bool depthClipControlFeatureSupported);
        bool enableDepthClipControlFeature = depthClipControlExtensionEnabled && depthClipControlFeatureSupported;

        bool meshShaderExtensionEnabled = extensionsArray.Contains("VK_EXT_mesh_shader");
        QueryMeshShaderCapabilities(
            meshShaderExtensionEnabled,
            out bool taskShaderFeatureSupported,
            out bool meshShaderFeatureSupported);
        bool enableMeshShaderFeature =
            meshShaderExtensionEnabled &&
            taskShaderFeatureSupported &&
            meshShaderFeatureSupported;

        bool graphicsPipelineLibraryDependencyEnabled = extensionsArray.Contains("VK_KHR_pipeline_library");
        bool graphicsPipelineLibraryExtensionEnabled =
            graphicsPipelineLibraryDependencyEnabled &&
            extensionsArray.Contains("VK_EXT_graphics_pipeline_library");
        QueryGraphicsPipelineLibraryCapabilities(
            graphicsPipelineLibraryExtensionEnabled,
            out bool graphicsPipelineLibraryFeatureSupported);
        bool enableGraphicsPipelineLibraryFeature =
            graphicsPipelineLibraryExtensionEnabled &&
            graphicsPipelineLibraryFeatureSupported;

        bool pipelineCreationCacheControlAvailable =
            vulkan13PromotedToCore ||
            extensionsArray.Contains("VK_EXT_pipeline_creation_cache_control");
        PhysicalDevicePipelineCreationCacheControlFeatures supportedPipelineCreationCacheControlFeatures = new()
        {
            SType = StructureType.PhysicalDevicePipelineCreationCacheControlFeatures,
        };
        if (pipelineCreationCacheControlAvailable)
        {
            PhysicalDeviceFeatures2 pipelineCreationCacheControlFeatures2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &supportedPipelineCreationCacheControlFeatures,
            };
            Api.GetPhysicalDeviceFeatures2(_physicalDevice, &pipelineCreationCacheControlFeatures2);
        }
        bool enablePipelineCreationCacheControlFeature =
            pipelineCreationCacheControlAvailable &&
            supportedPipelineCreationCacheControlFeatures.PipelineCreationCacheControl;
        _supportsPipelineCreationCacheControl = enablePipelineCreationCacheControlFeature;

        bool transformFeedbackExtensionEnabled = extensionsArray.Contains(ExtTransformFeedback.ExtensionName);
        QueryTransformFeedbackCapabilities(
            transformFeedbackExtensionEnabled,
            out bool transformFeedbackFeatureSupported,
            out bool transformFeedbackGeometryStreamsSupported,
            out PhysicalDeviceTransformFeedbackPropertiesEXT transformFeedbackProperties);
        bool enableTransformFeedbackFeature =
            transformFeedbackExtensionEnabled &&
            transformFeedbackFeatureSupported;

        bool fragmentShadingRateExtensionEnabled = extensionsArray.Contains("VK_KHR_fragment_shading_rate");
        QueryFragmentShadingRateCapabilities(
            fragmentShadingRateExtensionEnabled,
            out bool fragmentShadingRateFeatureSupported,
            out bool pipelineFragmentShadingRateSupported,
            out bool primitiveFragmentShadingRateSupported,
            out bool attachmentFragmentShadingRateSupported,
            out PhysicalDeviceFragmentShadingRatePropertiesKHR fragmentShadingRateProperties);
        bool enableFragmentShadingRateFeature =
            fragmentShadingRateExtensionEnabled &&
            fragmentShadingRateFeatureSupported;

        bool fragmentDensityMapExtensionEnabled = extensionsArray.Contains("VK_EXT_fragment_density_map");
        QueryFragmentDensityMapCapabilities(
            fragmentDensityMapExtensionEnabled,
            out bool fragmentDensityMapFeatureSupported,
            out bool fragmentDensityMapDynamicSupported,
            out bool fragmentDensityMapNonSubsampledImagesSupported);
        bool enableFragmentDensityMapFeature =
            fragmentDensityMapExtensionEnabled &&
            fragmentDensityMapFeatureSupported;

        _nvMemoryDecompressionMethods = enableNvMemoryDecompression ? nvMemoryDecompressionMethods : 0;
        _nvMaxMemoryDecompressionIndirectCount = enableNvMemoryDecompression ? nvMaxDecompressionIndirectCount : 0;
        _nvCopyMemoryIndirectSupportedQueues = enableNvCopyMemoryIndirect ? nvCopyMemoryIndirectSupportedQueues : 0;

        PhysicalDeviceDescriptorIndexingFeatures descriptorIndexingFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
            PNext = null,
            RuntimeDescriptorArray = enableDescriptorIndexing,
            DescriptorBindingPartiallyBound = enableDescriptorIndexing,
            DescriptorBindingSampledImageUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingStorageImageUpdateAfterBind = enableDescriptorIndexing && _supportsDescriptorBindingStorageImageUpdateAfterBind,
            DescriptorBindingStorageBufferUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingUniformBufferUpdateAfterBind = enableDescriptorIndexing,
        };

        PhysicalDeviceMemoryDecompressionFeaturesNV memoryDecompressionFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceMemoryDecompressionFeaturesNV,
            PNext = null,
            MemoryDecompression = enableNvMemoryDecompression,
        };

        PhysicalDeviceCopyMemoryIndirectFeaturesNV copyMemoryIndirectFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceCopyMemoryIndirectFeaturesNV,
            PNext = null,
            IndirectCopy = enableNvCopyMemoryIndirect,
        };

        PhysicalDeviceBufferDeviceAddressFeatures bufferDeviceAddressFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            PNext = null,
            BufferDeviceAddress = enableBufferDeviceAddress,
        };

        PhysicalDeviceDynamicRenderingFeatures dynamicRenderingFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            PNext = null,
            DynamicRendering = enableDynamicRenderingFeature,
        };

        PhysicalDeviceDynamicRenderingLocalReadFeatures dynamicRenderingLocalReadFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingLocalReadFeatures,
            PNext = null,
            DynamicRenderingLocalRead = enableDynamicRenderingLocalReadFeature,
        };

        PhysicalDeviceDynamicRenderingLocalReadFeaturesKHR dynamicRenderingLocalReadFeatureEnableKhr = new()
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingLocalReadFeaturesKhr,
            PNext = null,
            DynamicRenderingLocalRead = enableDynamicRenderingLocalReadFeature,
        };

        PhysicalDeviceVulkan11Features vulkan11FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = null,
            ShaderDrawParameters = enableShaderDrawParametersFeature,
            Multiview = enableMultiviewFeature,
        };

        PhysicalDeviceVulkan12Features vulkan12FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = null,
            DrawIndirectCount = enableDrawIndirectCountFeature,
            DescriptorIndexing = descriptorIndexingExtensionEnabled && supportedVulkan12Features.DescriptorIndexing,
            RuntimeDescriptorArray = enableDescriptorIndexing,
            DescriptorBindingPartiallyBound = enableDescriptorIndexing,
            DescriptorBindingSampledImageUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingStorageImageUpdateAfterBind = enableDescriptorIndexing && _supportsDescriptorBindingStorageImageUpdateAfterBind,
            DescriptorBindingStorageBufferUpdateAfterBind = enableDescriptorIndexing,
            DescriptorBindingUniformBufferUpdateAfterBind = enableDescriptorIndexing,
            TimelineSemaphore = enableTimelineSemaphoreFeature,
            BufferDeviceAddress = enableBufferDeviceAddress,
            ShaderOutputViewportIndex = enableShaderOutputViewportIndexFeature,
            ShaderOutputLayer = enableShaderOutputLayerFeature,
            HostQueryReset = enableHostQueryResetFeature,
        };

        PhysicalDeviceVulkan13Features vulkan13FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            PNext = null,
            DynamicRendering = enableDynamicRenderingFeature,
            Synchronization2 = enableSynchronization2Feature,
            Maintenance4 = enableMaintenance4Feature,
            PipelineCreationCacheControl = enablePipelineCreationCacheControlFeature,
        };

        PhysicalDeviceIndexTypeUint8FeaturesEXT indexTypeUint8FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceIndexTypeUint8FeaturesExt,
            PNext = null,
            IndexTypeUint8 = enableIndexTypeUint8Feature,
        };

        PhysicalDeviceMaintenance4Features maintenance4FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceMaintenance4Features,
            PNext = null,
            Maintenance4 = enableMaintenance4Feature,
        };

        PhysicalDeviceMaintenance5Features maintenance5FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceMaintenance5Features,
            PNext = null,
            Maintenance5 = enableMaintenance5Feature,
        };

        PhysicalDeviceMaintenance5FeaturesKHR maintenance5FeatureEnableKhr = new()
        {
            SType = StructureType.PhysicalDeviceMaintenance5FeaturesKhr,
            PNext = null,
            Maintenance5 = enableMaintenance5Feature,
        };

        PhysicalDeviceTimelineSemaphoreFeatures timelineSemaphoreFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceTimelineSemaphoreFeatures,
            PNext = null,
            TimelineSemaphore = enableTimelineSemaphoreFeature,
        };

        PhysicalDeviceHostQueryResetFeatures hostQueryResetFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceHostQueryResetFeatures,
            PNext = null,
            HostQueryReset = enableHostQueryResetFeature,
        };

        PhysicalDeviceSynchronization2Features synchronization2FeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceSynchronization2Features,
            PNext = null,
            Synchronization2 = enableSynchronization2Feature,
        };

        PhysicalDeviceDepthClipControlFeaturesEXTNative depthClipControlFeatureEnable = new()
        {
            SType = VulkanDepthClipControlExt.PhysicalDeviceFeaturesSType,
            PNext = null,
            DepthClipControl = enableDepthClipControlFeature,
        };

        PhysicalDeviceMeshShaderFeaturesEXT meshShaderFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt,
            PNext = null,
            TaskShader = enableMeshShaderFeature,
            MeshShader = enableMeshShaderFeature,
        };

        PhysicalDeviceGraphicsPipelineLibraryFeaturesEXT graphicsPipelineLibraryFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceGraphicsPipelineLibraryFeaturesExt,
            PNext = null,
            GraphicsPipelineLibrary = enableGraphicsPipelineLibraryFeature,
        };

        PhysicalDevicePipelineCreationCacheControlFeatures pipelineCreationCacheControlFeatureEnable = new()
        {
            SType = StructureType.PhysicalDevicePipelineCreationCacheControlFeatures,
            PNext = null,
            PipelineCreationCacheControl = enablePipelineCreationCacheControlFeature,
        };

        PhysicalDeviceTransformFeedbackFeaturesEXT transformFeedbackFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
            PNext = null,
            TransformFeedback = enableTransformFeedbackFeature,
            GeometryStreams = enableTransformFeedbackFeature && transformFeedbackGeometryStreamsSupported,
        };

        PhysicalDeviceFragmentShadingRateFeaturesKHR fragmentShadingRateFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceFragmentShadingRateFeaturesKhr,
            PNext = null,
            PipelineFragmentShadingRate = enableFragmentShadingRateFeature && pipelineFragmentShadingRateSupported,
            PrimitiveFragmentShadingRate = enableFragmentShadingRateFeature && primitiveFragmentShadingRateSupported,
            AttachmentFragmentShadingRate = enableFragmentShadingRateFeature && attachmentFragmentShadingRateSupported,
        };

        PhysicalDeviceFragmentDensityMapFeaturesEXT fragmentDensityMapFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceFragmentDensityMapFeaturesExt,
            PNext = null,
            FragmentDensityMap = enableFragmentDensityMapFeature,
            FragmentDensityMapDynamic = enableFragmentDensityMapFeature && fragmentDensityMapDynamicSupported,
            FragmentDensityMapNonSubsampledImages = enableFragmentDensityMapFeature && fragmentDensityMapNonSubsampledImagesSupported,
        };

        PhysicalDeviceFaultFeaturesEXT deviceFaultFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceFaultFeaturesExt,
            PNext = null,
            DeviceFault = enableExtDeviceFaultFeature,
            DeviceFaultVendorBinary = enableExtDeviceFaultFeature && extDeviceFaultVendorBinaryFeatureSupported,
        };

        VulkanKhrPhysicalDeviceFaultFeatures khrDeviceFaultFeatureEnable = new()
        {
            SType = KhrStructureType(VulkanKhrDeviceFaultPhysicalDeviceFeaturesSType),
            PNext = null,
            DeviceFault = ToVulkanBool(enableKhrDeviceFaultFeature),
            DeviceFaultVendorBinary = ToVulkanBool(enableKhrDeviceFaultVendorBinary),
            DeviceFaultReportMasked = ToVulkanBool(enableKhrDeviceFaultReportMasked),
            DeviceFaultDeviceLostOnMasked = ToVulkanBool(enableKhrDeviceFaultDeviceLostOnMasked),
        };

        PhysicalDeviceAddressBindingReportFeaturesEXT deviceAddressBindingReportFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceAddressBindingReportFeaturesExt,
            PNext = null,
            ReportAddressBinding = enableDeviceAddressBindingReportFeature,
        };

        PhysicalDeviceDiagnosticsConfigFeaturesNV nvDiagnosticsConfigFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceDiagnosticsConfigFeaturesNV,
            PNext = null,
            DiagnosticsConfig = enableNvDiagnosticsConfigFeature,
        };

        DeviceDiagnosticsConfigCreateInfoNV nvDiagnosticsConfigCreateInfo = new()
        {
            SType = StructureType.DeviceDiagnosticsConfigCreateInfoNV,
            PNext = null,
            Flags = DeviceDiagnosticsConfigFlagsNV.ResourceTrackingBitNV |
                    DeviceDiagnosticsConfigFlagsNV.AutomaticCheckpointsBitNV |
                    DeviceDiagnosticsConfigFlagsNV.ShaderErrorReportingBitNV,
        };

        PhysicalDeviceDescriptorHeapFeaturesEXTNative descriptorHeapFeatureEnable = new()
        {
            SType = VulkanDescriptorHeapExt.PhysicalDeviceDescriptorHeapFeaturesSType,
            PNext = null,
            DescriptorHeap = enableDescriptorHeapFeature,
            DescriptorHeapCaptureReplay = false,
        };

        // Keep promoted feature structs separate. Mixing VkPhysicalDeviceVulkan12/13Features
        // with their promoted per-feature structs is invalid. Streamline reports feature names
        // in the aggregate 1.2/1.3 structs, so consolidate existing engine features into those
        // aggregate structs whenever Streamline requires one of them.
        bool useVulkan12FeatureEnable = _streamlineRequiredFeatures12.Length > 0;
        bool useVulkan13FeatureEnable = _streamlineRequiredFeatures13.Length > 0;
        if (useVulkan12FeatureEnable || useVulkan13FeatureEnable)
        {
            PhysicalDeviceVulkan12Features streamlineSupportedFeatures12 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
            };
            PhysicalDeviceVulkan13Features streamlineSupportedFeatures13 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan13Features,
            };
            PhysicalDeviceFeatures2 streamlineSupportedFeatures = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };

            if (useVulkan12FeatureEnable)
            {
                streamlineSupportedFeatures.PNext = &streamlineSupportedFeatures12;
                streamlineSupportedFeatures12.PNext = useVulkan13FeatureEnable ? &streamlineSupportedFeatures13 : null;
            }
            else
            {
                streamlineSupportedFeatures.PNext = &streamlineSupportedFeatures13;
            }

            Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &streamlineSupportedFeatures);
            if (useVulkan12FeatureEnable)
            {
                PopulateStreamlineRequiredFeatures(
                    ref vulkan12FeatureEnable,
                    in streamlineSupportedFeatures12,
                    _streamlineRequiredFeatures12,
                    "Vulkan 1.2");
            }

            if (useVulkan13FeatureEnable)
            {
                PopulateStreamlineRequiredFeatures(
                    ref vulkan13FeatureEnable,
                    in streamlineSupportedFeatures13,
                    _streamlineRequiredFeatures13,
                    "Vulkan 1.3");
            }
        }

        bool enableStreamlineOpticalFlow = _streamlineQueueRequirements.OpticalFlowQueues > 0;
        PhysicalDeviceOpticalFlowFeaturesNV opticalFlowFeatureEnable = new()
        {
            SType = StructureType.PhysicalDeviceOpticalFlowFeaturesNV,
            PNext = null,
            OpticalFlow = enableStreamlineOpticalFlow,
        };

        if (enableStreamlineOpticalFlow)
        {
            PhysicalDeviceOpticalFlowFeaturesNV supportedOpticalFlow = new()
            {
                SType = StructureType.PhysicalDeviceOpticalFlowFeaturesNV,
            };
            PhysicalDeviceFeatures2 supportedOpticalFlowFeatures = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &supportedOpticalFlow,
            };
            Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &supportedOpticalFlowFeatures);
            if (!supportedOpticalFlow.OpticalFlow)
                throw new NotSupportedException("Streamline DLSS-G requires VkPhysicalDeviceOpticalFlowFeaturesNV::opticalFlow, but the selected Vulkan device does not support it.");
        }

        void* enabledFeaturesPNext = null;
        if (enableDescriptorIndexing && !useVulkan12FeatureEnable)
        {
            descriptorIndexingFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &descriptorIndexingFeatureEnable;
        }

        if (enableNvMemoryDecompression)
        {
            memoryDecompressionFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &memoryDecompressionFeatureEnable;
        }

        if (enableNvCopyMemoryIndirect)
        {
            copyMemoryIndirectFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &copyMemoryIndirectFeatureEnable;
        }

        if (enableBufferDeviceAddress && !useVulkan12FeatureEnable)
        {
            bufferDeviceAddressFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &bufferDeviceAddressFeatureEnable;
        }

        if (enableDescriptorHeapFeature)
        {
            descriptorHeapFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &descriptorHeapFeatureEnable;
        }

        if (enableDynamicRenderingFeature && !useVulkan13FeatureEnable)
        {
            dynamicRenderingFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &dynamicRenderingFeatureEnable;
        }

        if (enableDynamicRenderingLocalReadFeature)
        {
            if (dynamicRenderingLocalReadPromotedToCore)
            {
                dynamicRenderingLocalReadFeatureEnable.PNext = enabledFeaturesPNext;
                enabledFeaturesPNext = &dynamicRenderingLocalReadFeatureEnable;
            }
            else
            {
                dynamicRenderingLocalReadFeatureEnableKhr.PNext = enabledFeaturesPNext;
                enabledFeaturesPNext = &dynamicRenderingLocalReadFeatureEnableKhr;
            }
        }

        if (enableShaderDrawParametersFeature || enableMultiviewFeature)
        {
            vulkan11FeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &vulkan11FeatureEnable;
        }

        if (useVulkan12FeatureEnable)
        {
            vulkan12FeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &vulkan12FeatureEnable;
        }

        if (useVulkan13FeatureEnable)
        {
            vulkan13FeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &vulkan13FeatureEnable;
        }

        if (enableIndexTypeUint8Feature)
        {
            indexTypeUint8FeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &indexTypeUint8FeatureEnable;
        }

        if (enableMaintenance4Feature && !useVulkan13FeatureEnable)
        {
            maintenance4FeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &maintenance4FeatureEnable;
        }

        if (enableMaintenance5Feature)
        {
            if (maintenance5PromotedToCore)
            {
                maintenance5FeatureEnable.PNext = enabledFeaturesPNext;
                enabledFeaturesPNext = &maintenance5FeatureEnable;
            }
            else
            {
                maintenance5FeatureEnableKhr.PNext = enabledFeaturesPNext;
                enabledFeaturesPNext = &maintenance5FeatureEnableKhr;
            }
        }

        if (enableTimelineSemaphoreFeature && !useVulkan12FeatureEnable)
        {
            timelineSemaphoreFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &timelineSemaphoreFeatureEnable;
        }

        if (enableHostQueryResetFeature && !useVulkan12FeatureEnable)
        {
            hostQueryResetFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &hostQueryResetFeatureEnable;
        }

        if (enableSynchronization2Feature && !useVulkan13FeatureEnable)
        {
            synchronization2FeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &synchronization2FeatureEnable;
        }

        if (enableDepthClipControlFeature)
        {
            depthClipControlFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &depthClipControlFeatureEnable;
        }

        if (enableMeshShaderFeature)
        {
            meshShaderFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &meshShaderFeatureEnable;
        }

        if (enableGraphicsPipelineLibraryFeature)
        {
            graphicsPipelineLibraryFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &graphicsPipelineLibraryFeatureEnable;
        }

        if (enablePipelineCreationCacheControlFeature && !useVulkan13FeatureEnable)
        {
            pipelineCreationCacheControlFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &pipelineCreationCacheControlFeatureEnable;
        }

        if (enableTransformFeedbackFeature)
        {
            transformFeedbackFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &transformFeedbackFeatureEnable;
        }

        if (enableFragmentShadingRateFeature)
        {
            fragmentShadingRateFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &fragmentShadingRateFeatureEnable;
        }

        if (enableFragmentDensityMapFeature)
        {
            fragmentDensityMapFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &fragmentDensityMapFeatureEnable;
        }

        if (enableExtDeviceFaultFeature)
        {
            deviceFaultFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &deviceFaultFeatureEnable;
        }

        if (enableKhrDeviceFaultFeature)
        {
            khrDeviceFaultFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &khrDeviceFaultFeatureEnable;
        }

        if (enableDeviceAddressBindingReportFeature)
        {
            deviceAddressBindingReportFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &deviceAddressBindingReportFeatureEnable;
        }

        if (enableNvDiagnosticsConfigFeature)
        {
            nvDiagnosticsConfigFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &nvDiagnosticsConfigFeatureEnable;
        }

        if (enableStreamlineOpticalFlow)
        {
            opticalFlowFeatureEnable.PNext = enabledFeaturesPNext;
            enabledFeaturesPNext = &opticalFlowFeatureEnable;
        }

        PhysicalDeviceFeatures2 featureChain = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = enabledFeaturesPNext,
            Features = deviceFeatures,
        };

        void* deviceCreatePNext = &featureChain;
        if (enableNvDiagnosticsConfigFeature)
        {
            nvDiagnosticsConfigCreateInfo.PNext = deviceCreatePNext;
            deviceCreatePNext = &nvDiagnosticsConfigCreateInfo;
        }

        // Configure the logical device creation
        DeviceCreateInfo createInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,

            PNext = deviceCreatePNext,
            PEnabledFeatures = null,

            // Enable required device extensions (e.g., swapchain)
            EnabledExtensionCount = (uint)extensionsArray.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensionsArray)
        };

        // Device layers are deprecated/invalid in modern Vulkan. Validation is enabled at instance creation.
        createInfo.EnabledLayerCount = 0;
        createInfo.PpEnabledLayerNames = null;

        var getInstanceProcAddr = Api!.GetInstanceProcAddr(default, "vkGetInstanceProcAddr");
        if (_openXrVulkanEnable2Context is not null)
        {
            if (_openXrVulkanEnable2Context.TryCreateVulkanDevice(
                (nint)_physicalDevice.Handle,
                &createInfo,
                getInstanceProcAddr,
                out nint openXrCreatedDeviceHandle,
                out _,
                out string? openXrCreateFailure))
            {
                device = new Device(openXrCreatedDeviceHandle);
                _vulkanDeviceCreatedThroughOpenXr = true;
            }
            else
            {
                throw new Exception($"Failed to create Vulkan logical device through OpenXR: {openXrCreateFailure}");
            }
        }
        else
        {
            // Create the logical device
            if (Api.CreateDevice(_physicalDevice, in createInfo, null, out device) != Result.Success)
                throw new Exception("Failed to create logical device.");

            _vulkanDeviceCreatedThroughOpenXr = false;
        }

        bool descriptorHeapNativeApiAvailable = false;
        string descriptorHeapNativeApiReason = string.Empty;
        if (enableDescriptorHeapFeature)
            descriptorHeapNativeApiAvailable = TryInitializeDescriptorHeapNativeApi(out descriptorHeapNativeApiReason);

        _supportsDescriptorIndexing = enableDescriptorIndexing;
        _supportsNvMemoryDecompression = enableNvMemoryDecompression;
        _supportsNvCopyMemoryIndirect = enableNvCopyMemoryIndirect;
        _supportsBufferDeviceAddress = enableBufferDeviceAddress;
        _supportsDynamicRendering = dynamicRenderingFeatureSupported;
        _supportsVulkan14 = vulkan14PromotedToCore;
        _supportsDynamicRenderingLocalRead = enableDynamicRenderingLocalReadFeature;
        _supportsDynamicRenderingLocalReadStorageResources = enableDynamicRenderingLocalReadFeature;
        _supportsDynamicRenderingLocalReadColorAttachments = enableDynamicRenderingLocalReadFeature;
        _supportsDynamicRenderingLocalReadDepthStencilAttachments =
            enableDynamicRenderingLocalReadFeature && dynamicRenderingLocalReadDepthStencilSupported;
        _supportsDynamicRenderingLocalReadMultisampledAttachments =
            enableDynamicRenderingLocalReadFeature && dynamicRenderingLocalReadMultisampledSupported;
        _supportsMaintenance4 = enableMaintenance4Feature;
        _supportsMaintenance5 = enableMaintenance5Feature;
        _supportsExtendedFlags = extendedFlagsExtensionEnabled;
        _descriptorHeapFeatureSupported = descriptorHeapFeatureSupported;
        _descriptorHeapCaptureReplaySupported = descriptorHeapCaptureReplaySupported;
        _descriptorHeapProperties = descriptorHeapFeatureSupported ? descriptorHeapProperties : default;
        _supportsDescriptorHeap = enableDescriptorHeapFeature && descriptorHeapNativeApiAvailable;
        _supportsShaderObject = shaderObjectFeatureSupported;
        _supportsMemoryBudget = memoryBudgetExtensionAvailable && memoryBudgetExtensionEnabled;
        _supportsMemoryPriority = memoryPriorityFeatureSupported;
        _supportsAccelerationStructure = accelerationStructureFeatureSupported;
        _supportsRayTracingPipeline = rayTracingPipelineFeatureSupported;
        _supportsRayQuery = rayQueryFeatureSupported;
        _supportsDeviceGeneratedCommands = deviceGeneratedCommandsFeatureSupported;
        _supportsKhrDeviceFault = enableKhrDeviceFaultFeature;
        _supportsKhrDeviceFaultVendorBinary = enableKhrDeviceFaultVendorBinary;
        _supportsKhrDeviceFaultReportMasked = enableKhrDeviceFaultReportMasked;
        _supportsKhrDeviceFaultDeviceLostOnMasked = enableKhrDeviceFaultDeviceLostOnMasked;
        _khrDeviceFaultMaxReportCount = khrDeviceFaultMaxReportCount;
        _supportsExtDeviceFault = enableExtDeviceFaultFeature;
        _supportsExtDeviceFaultVendorBinary = enableExtDeviceFaultFeature && extDeviceFaultVendorBinaryFeatureSupported;
        _supportsDeviceFault = enableDeviceFaultFeature;
        _supportsDeviceFaultVendorBinary = _supportsKhrDeviceFaultVendorBinary || _supportsExtDeviceFaultVendorBinary;
        _supportsDeviceAddressBindingReport = enableDeviceAddressBindingReportFeature;
        _supportsNvDiagnosticCheckpoints = enableNvDiagnosticCheckpoints;
        _supportsNvDiagnosticsConfig = enableNvDiagnosticsConfigFeature;
        _shaderObjectProperties = shaderObjectFeatureSupported ? shaderObjectProperties : default;
        _supportsIndexTypeUint8 = enableIndexTypeUint8Feature;
        _supportsTimelineSemaphores = enableTimelineSemaphoreFeature;
        _supportsSynchronization2 = enableSynchronization2Feature;
        _supportsDepthClipControl = enableDepthClipControlFeature;
        _supportsGraphicsPipelineLibrary = enableGraphicsPipelineLibraryFeature;
        _supportsTransformFeedback = enableTransformFeedbackFeature;
        _supportsTransformFeedbackGeometryStreams = enableTransformFeedbackFeature && transformFeedbackGeometryStreamsSupported;
        _supportsTransformFeedbackQueries = enableTransformFeedbackFeature && transformFeedbackProperties.TransformFeedbackQueries;
        _supportsTransformFeedbackDraw = enableTransformFeedbackFeature && transformFeedbackProperties.TransformFeedbackDraw;
        _transformFeedbackProperties = enableTransformFeedbackFeature ? transformFeedbackProperties : default;
        _supportsHostQueryReset = enableHostQueryResetFeature;
        _supportsVulkanFragmentShadingRate = enableFragmentShadingRateFeature;
        _supportsVulkanFragmentShadingRateAttachment = enableFragmentShadingRateFeature && attachmentFragmentShadingRateSupported;
        _fragmentShadingRateProperties = enableFragmentShadingRateFeature ? fragmentShadingRateProperties : default;
        _supportsVulkanFragmentDensityMap = enableFragmentDensityMapFeature;
        _supportsVulkanFragmentDensityMapDynamic = enableFragmentDensityMapFeature && fragmentDensityMapDynamicSupported;
        _supportsVulkanTaskShaderFeature = enableMeshShaderFeature;
        _supportsVulkanMeshShaderFeature = enableMeshShaderFeature;

        // Load optional extension command tables before resolving backend modes that depend on them.
        LoadOptionalDeviceExtensions(extensionsArray, enableDrawIndirectCountFeature);
        LogVulkanDiagnosticDeviceCapabilities(
            khrDeviceFaultExtensionAvailable,
            khrDeviceFaultExtensionEnabled,
            khrDeviceFaultFeatureSupported,
            khrDeviceFaultVendorBinaryFeatureSupported,
            khrDeviceFaultReportMaskedFeatureSupported,
            khrDeviceFaultDeviceLostOnMaskedFeatureSupported,
            khrDeviceFaultMaxReportCount,
            extDeviceFaultExtensionAvailable,
            extDeviceFaultExtensionEnabled,
            extDeviceFaultFeatureSupported,
            extDeviceFaultVendorBinaryFeatureSupported,
            deviceAddressBindingReportExtensionAvailable,
            deviceAddressBindingReportExtensionEnabled,
            deviceAddressBindingReportFeatureSupported,
            nvDiagnosticCheckpointsExtensionAvailable,
            nvDiagnosticCheckpointsExtensionEnabled,
            nvDiagnosticsConfigExtensionAvailable,
            nvDiagnosticsConfigExtensionEnabled,
            nvDiagnosticsConfigFeatureSupported);

        ResolveDescriptorBackendAfterDeviceCreate(
            VulkanFeatureProfile.RequestedDescriptorBackend,
            enableDescriptorIndexing,
            descriptorHeapExtensionAvailable,
            descriptorHeapDependenciesReady,
            descriptorHeapFeatureSupported,
            descriptorHeapNativeApiAvailable);
        if (enableDescriptorHeapFeature && !descriptorHeapNativeApiAvailable)
        {
            Debug.VulkanWarning(
                "[Vulkan.DescriptorHeap.Capability] feature enabled but native API loading failed: {0}",
                descriptorHeapNativeApiReason);
        }
        ValidateExplicitModernBackendRequests(
            vulkan13PromotedToCore,
            vulkan14PromotedToCore,
            enableDynamicRenderingFeature,
            enableDynamicRenderingLocalReadFeature,
            enableMaintenance4Feature,
            enableMaintenance5Feature,
            enableSynchronization2Feature,
            enableTimelineSemaphoreFeature,
            enableDescriptorIndexing,
            enableBufferDeviceAddress,
            drawIndirectCountExtensionEnabled || enableDrawIndirectCountFeature,
            descriptorHeapExtensionAvailable,
            descriptorHeapDependenciesReady,
            shaderObjectFeatureSupported,
            enableFragmentShadingRateFeature,
            enableFragmentDensityMapFeature,
            accelerationStructureFeatureSupported,
            rayTracingPipelineFeatureSupported,
            rayQueryFeatureSupported);
        RuntimeEngine.Rendering.State.HasVulkanMultiView = enableMultiviewFeature;
        RuntimeEngine.Rendering.State.HasOvrMultiViewExtension = enableMultiviewFeature;
        RuntimeEngine.Rendering.State.HasVulkanDepthClipControl = enableDepthClipControlFeature;
        ReportLayeredShadowCapabilities(
            enableMultiViewport,
            enableShaderOutputViewportIndexFeature,
            enableShaderOutputLayerFeature);

        if (descriptorIndexingExtensionEnabled && !enableDescriptorIndexing)
        {
            Debug.VulkanWarning(
                "[Vulkan] Descriptor indexing extension present but disabled (requested={0}, runtimeArray={1}, partiallyBound={2}, updateAfterBind={3}).",
                descriptorIndexingRequestedByProfile,
                _supportsRuntimeDescriptorArray,
                _supportsDescriptorBindingPartiallyBound,
                _supportsDescriptorBindingUpdateAfterBind);
        }
            else if (enableDescriptorIndexing && !_supportsDescriptorBindingStorageImageUpdateAfterBind)
            {
                Debug.VulkanWarning(
                "[Vulkan] Descriptor indexing enabled without storage-image update-after-bind support; storage image bindings will not use UPDATE_AFTER_BIND flags.");
            }

            ValidateRequiredVulkanBindlessMaterialCapability();

            if (!enableShaderDrawParametersFeature && !shaderDrawParametersExtensionEnabled)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Draw parameters support unavailable (shaderDrawParametersFeature={0}, extensionEnabled={1}). Shaders using gl_BaseVertex/gl_BaseInstance may fail.",
                    shaderDrawParametersFeatureSupported,
                    shaderDrawParametersExtensionEnabled);
            }

            if (!enableShaderOutputViewportIndexFeature || !enableShaderOutputLayerFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Shader viewport/layer output support incomplete (viewportIndexFeature={0}, layerFeature={1}, promotedToCore={2}). Cascaded shadow atlas rendering may fall back.",
                    shaderOutputViewportIndexFeatureSupported,
                    shaderOutputLayerFeatureSupported,
                    shaderViewportLayerPromotedToCore);
            }

            if (depthClipControlExtensionEnabled && !enableDepthClipControlFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] {0} extension present but disabled because the depthClipControl feature bit is unavailable.",
                    VulkanDepthClipControlExt.ExtensionName);
            }

            if (!enableMultiviewFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Multiview support unavailable (featureSupported={0}, extensionEnabled={1}, promotedToCore={2}). Stereo single-pass multiview path will be disabled.",
                    multiviewFeatureSupported,
                    multiviewExtensionEnabled,
                    multiviewPromotedToCore);
            }

            if (nvMemoryDecompressionExtensionEnabled && !enableNvMemoryDecompression)
            {
                Debug.VulkanWarning(
                "[Vulkan] VK_NV_memory_decompression present but disabled (requested={0}, featureSupported={1}).",
                nvMemoryDecompressionRequestedByProfile,
                nvMemoryDecompressionFeatureSupported);
            }

            if (nvCopyMemoryIndirectExtensionEnabled && !enableNvCopyMemoryIndirect)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_NV_copy_memory_indirect present but disabled (requested={0}, featureSupported={1}).",
                    nvCopyMemoryIndirectRequestedByProfile,
                    nvCopyMemoryIndirectFeatureSupported);
            }

            if (enableNvCopyMemoryIndirect && !enableBufferDeviceAddress)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_NV_copy_memory_indirect enabled but buffer device address is unavailable; indirect copy commands will be disabled.");
            }

            if (bufferDeviceAddressRequestedBySceneDatabase && !enableBufferDeviceAddress)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Scene-database bufferDeviceAddress was requested (geometryFetch={0}, bindlessMaterialTable={1}) but the feature is unavailable.",
                    VulkanFeatureProfile.ActiveGeometryFetchMode,
                    VulkanFeatureProfile.EnableBindlessMaterialTable);
            }

            if (!enableIndexTypeUint8Feature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] UINT8 index type unsupported or disabled (featureSupported={0}, extensionEnabled={1}). Byte-sized index buffers will be skipped.",
                    indexTypeUint8FeatureSupported,
                    indexTypeUint8ExtensionEnabled);
            }

            if (!enableTimelineSemaphoreFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Timeline semaphores unsupported or disabled (featureSupported={0}). Renderer timeline synchronization path requires this feature.",
                    timelineSemaphoreFeatureSupported);
            }

            if (synchronization2ExtensionEnabled && !enableSynchronization2Feature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_KHR_synchronization2 present but disabled (featureSupported={0}). Renderer will remain on legacy barrier/submit APIs.",
                    _supportsSynchronization2Feature);
            }

            if (meshShaderExtensionEnabled && !enableMeshShaderFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_EXT_mesh_shader present but disabled (taskShaderFeature={0}, meshShaderFeature={1}). Production meshlet dispatch will remain unavailable.",
                    taskShaderFeatureSupported,
                    meshShaderFeatureSupported);
            }

            if (graphicsPipelineLibraryExtensionEnabled && !enableGraphicsPipelineLibraryFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] VK_EXT_graphics_pipeline_library present but disabled because the graphicsPipelineLibrary feature bit is unavailable.");
            }

            if (transformFeedbackExtensionEnabled && !enableTransformFeedbackFeature)
            {
                Debug.VulkanWarning(
                    "[Vulkan] {0} present but disabled because the transformFeedback feature bit is unavailable.",
                    ExtTransformFeedback.ExtensionName);
            }

            if (fragmentShadingRateExtensionEnabled)
            {
                if (enableFragmentShadingRateFeature)
                {
                    Debug.Vulkan(
                        "[Vulkan] VK_KHR_fragment_shading_rate enabled (pipeline={0}, primitive={1}, attachment={2}, attachmentTexelMin={3}x{4}, attachmentTexelMax={5}x{6}, maxFragment={7}x{8}, nonTrivialCombiner={9}, strictMultiplyCombiner={10}).",
                        pipelineFragmentShadingRateSupported,
                        primitiveFragmentShadingRateSupported,
                        attachmentFragmentShadingRateSupported,
                        fragmentShadingRateProperties.MinFragmentShadingRateAttachmentTexelSize.Width,
                        fragmentShadingRateProperties.MinFragmentShadingRateAttachmentTexelSize.Height,
                        fragmentShadingRateProperties.MaxFragmentShadingRateAttachmentTexelSize.Width,
                        fragmentShadingRateProperties.MaxFragmentShadingRateAttachmentTexelSize.Height,
                        fragmentShadingRateProperties.MaxFragmentSize.Width,
                        fragmentShadingRateProperties.MaxFragmentSize.Height,
                        fragmentShadingRateProperties.FragmentShadingRateNonTrivialCombinerOps,
                        fragmentShadingRateProperties.FragmentShadingRateStrictMultiplyCombiner);
                }
                else
                {
                    Debug.VulkanWarning(
                        "[Vulkan] VK_KHR_fragment_shading_rate present but disabled because no fragment shading-rate feature bit is available.");
                }
            }

            if (fragmentDensityMapExtensionEnabled)
            {
                if (enableFragmentDensityMapFeature)
                {
                    Debug.Vulkan(
                        "[Vulkan] VK_EXT_fragment_density_map enabled (dynamic={0}, nonSubsampledImages={1}).",
                        fragmentDensityMapDynamicSupported,
                        fragmentDensityMapNonSubsampledImagesSupported);
                }
                else
                {
                    Debug.VulkanWarning(
                        "[Vulkan] VK_EXT_fragment_density_map present but disabled because the fragmentDensityMap feature bit is unavailable.");
                }
            }

        // Retrieve handles to the queues we need
        Api!.GetDeviceQueue(device, indices.GraphicsFamilyIndex!.Value, 0, out graphicsQueue);
        if (_supportsMultipleGraphicsQueues)
            Api!.GetDeviceQueue(device, indices.GraphicsFamilyIndex!.Value, 1, out secondaryGraphicsQueue);
        else
            secondaryGraphicsQueue = default;

        Api!.GetDeviceQueue(device, indices.PresentFamilyIndex!.Value, 0, out presentQueue);
        Api!.GetDeviceQueue(device, indices.ComputeFamilyIndex ?? indices.GraphicsFamilyIndex!.Value, 0, out computeQueue);
        Api!.GetDeviceQueue(device, indices.TransferFamilyIndex ?? indices.ComputeFamilyIndex ?? indices.GraphicsFamilyIndex!.Value, 0, out transferQueue);

        // Clean up allocated memory for extension names
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
    }

    /// <summary>
    /// Loads optional device extension handles after device creation.
    /// </summary>
    private void LoadOptionalDeviceExtensions(
        string[] enabledExtensions,
        bool enableDrawIndirectCountFeature)
    {
        bool descriptorIndexingExtensionLoaded = enabledExtensions.Contains("VK_EXT_descriptor_indexing");

        if (enabledExtensions.Contains("VK_KHR_dynamic_rendering") &&
            (!UseCoreDynamicRenderingCommands || _streamlineFrameGenerationProvisioned))
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _khrDynamicRendering))
            {
                Debug.Vulkan(
                    "[Vulkan] VK_KHR_dynamic_rendering extension command table loaded for Vulkan instance API {0}.",
                    FormatVulkanApiVersion(_vulkanInstanceApiVersion));
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_dynamic_rendering extension command table.");
                _supportsDynamicRendering = false;
            }
        }

        if (enabledExtensions.Contains("VK_KHR_synchronization2") && !UseCoreSynchronization2Commands)
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _khrSynchronization2))
            {
                Debug.Vulkan(
                    "[Vulkan] VK_KHR_synchronization2 extension command table loaded for Vulkan instance API {0}.",
                    FormatVulkanApiVersion(_vulkanInstanceApiVersion));
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_synchronization2 extension command table.");
                _supportsSynchronization2 = false;
            }
        }

        bool indirectCountCoreFeaturesReady =
            _supportsMultiDrawIndirect &&
            _supportsDrawIndirectFirstInstance;
        if (enableDrawIndirectCountFeature && indirectCountCoreFeaturesReady)
        {
            _usesCoreDrawIndirectCountCommands = true;
            _supportsDrawIndirectCount = true;
            Debug.Vulkan("[Vulkan] Vulkan 1.2 core indirect-count drawing enabled with required core indirect features.");
        }
        // Vulkan 1.1 and older expose the command through VK_KHR_draw_indirect_count.
        else if (enabledExtensions.Contains("VK_KHR_draw_indirect_count"))
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _khrDrawIndirectCount))
            {
                _supportsDrawIndirectCount = indirectCountCoreFeaturesReady;
                if (_supportsDrawIndirectCount)
                {
                    Debug.Vulkan("[Vulkan] VK_KHR_draw_indirect_count extension loaded with required core indirect features.");
                }
                else
                {
                    Debug.VulkanWarning(
                        "[Vulkan] VK_KHR_draw_indirect_count loaded but disabled for engine submission " +
                        "because required core features are unavailable (multiDrawIndirect={0}, drawIndirectFirstInstance={1}).",
                        _supportsMultiDrawIndirect,
                        _supportsDrawIndirectFirstInstance);
                }
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_draw_indirect_count extension handle.");
                _supportsDrawIndirectCount = false;
            }
        }

        if (enabledExtensions.Contains(ExtMeshShader.ExtensionName))
        {
            if (_supportsVulkanTaskShaderFeature &&
                _supportsVulkanMeshShaderFeature &&
                Api!.TryGetDeviceExtension(instance, device, out _extMeshShader))
            {
                _supportsVulkanMeshTaskIndirectCount = true;
                Debug.Vulkan("[Vulkan] VK_EXT_mesh_shader extension loaded successfully for indirect-count mesh task dispatch.");
            }
            else
            {
                Debug.VulkanWarning(
                    "[Vulkan] Failed to load VK_EXT_mesh_shader for production dispatch (taskFeature={0}, meshFeature={1}).",
                    _supportsVulkanTaskShaderFeature,
                    _supportsVulkanMeshShaderFeature);
                _supportsVulkanMeshTaskIndirectCount = false;
            }
        }

        if (enabledExtensions.Contains(ExtTransformFeedback.ExtensionName))
        {
            if (_supportsTransformFeedback &&
                Api!.TryGetDeviceExtension(instance, device, out _extTransformFeedback))
            {
                Debug.Vulkan(
                    "[Vulkan] {0} loaded successfully (buffers={1}, maxBufferSize={2}, queries={3}, draw={4}, geometryStreams={5}).",
                    ExtTransformFeedback.ExtensionName,
                    _transformFeedbackProperties.MaxTransformFeedbackBuffers,
                    _transformFeedbackProperties.MaxTransformFeedbackBufferSize,
                    _supportsTransformFeedbackQueries,
                    _supportsTransformFeedbackDraw,
                    _supportsTransformFeedbackGeometryStreams);
            }
            else
            {
                Debug.VulkanWarning(
                    "[Vulkan] Failed to load {0} extension handle or feature was disabled.",
                    ExtTransformFeedback.ExtensionName);
                _supportsTransformFeedback = false;
                _supportsTransformFeedbackGeometryStreams = false;
                _supportsTransformFeedbackQueries = false;
                _supportsTransformFeedbackDraw = false;
                _transformFeedbackProperties = default;
            }
        }

        if (enabledExtensions.Contains("VK_KHR_external_memory_win32"))
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _khrExternalMemoryWin32))
            {
                _supportsExternalMemoryWin32 = true;
                Debug.Vulkan("[Vulkan] VK_KHR_external_memory_win32 extension loaded successfully.");
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_external_memory_win32 extension handle.");
                _supportsExternalMemoryWin32 = false;
            }
        }

        if (enabledExtensions.Contains("VK_KHR_external_semaphore_win32"))
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _khrExternalSemaphoreWin32))
            {
                _supportsExternalSemaphoreWin32 = true;
                Debug.Vulkan("[Vulkan] VK_KHR_external_semaphore_win32 extension loaded successfully.");
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_KHR_external_semaphore_win32 extension handle.");
                _supportsExternalSemaphoreWin32 = false;
            }
        }

        if (enabledExtensions.Contains("VK_NV_memory_decompression") && _supportsNvMemoryDecompression)
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _nvMemoryDecompression))
            {
                _supportsNvMemoryDecompression = true;
                Debug.Vulkan(
                    "[Vulkan] VK_NV_memory_decompression loaded successfully (methodsMask=0x{0:X}, maxIndirectCount={1}).",
                    _nvMemoryDecompressionMethods,
                    _nvMaxMemoryDecompressionIndirectCount);
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_NV_memory_decompression extension handle.");
                _supportsNvMemoryDecompression = false;
                _nvMemoryDecompressionMethods = 0;
                _nvMaxMemoryDecompressionIndirectCount = 0;
            }
        }

        if (enabledExtensions.Contains("VK_NV_copy_memory_indirect") && _supportsNvCopyMemoryIndirect)
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _nvCopyMemoryIndirect))
            {
                _supportsNvCopyMemoryIndirect = true;
                Debug.Vulkan(
                    "[Vulkan] VK_NV_copy_memory_indirect loaded successfully (supportedQueuesMask=0x{0:X}).",
                    _nvCopyMemoryIndirectSupportedQueues);
            }
            else
            {
                Debug.VulkanWarning("[Vulkan] Failed to load VK_NV_copy_memory_indirect extension handle.");
                _supportsNvCopyMemoryIndirect = false;
                _nvCopyMemoryIndirectSupportedQueues = 0;
            }
        }

        if (enabledExtensions.Contains(KhrDeviceFaultExtensionName) && _supportsKhrDeviceFault)
        {
            if (!TryLoadKhrDeviceFaultFunctionPointers())
            {
                _supportsKhrDeviceFault = false;
                _supportsKhrDeviceFaultVendorBinary = false;
                _supportsKhrDeviceFaultReportMasked = false;
                _supportsKhrDeviceFaultDeviceLostOnMasked = false;
                _deviceFaultUsingKhr = false;
                _supportsDeviceFault = _supportsExtDeviceFault;
                _supportsDeviceFaultVendorBinary = _supportsExtDeviceFaultVendorBinary;
            }
        }

        if (enabledExtensions.Contains(ExtDeviceFaultExtensionName) && _supportsExtDeviceFault)
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _extDeviceFault))
            {
                Debug.Vulkan(
                    "[VulkanDiag] DeviceFaultEXT compatibility active extension={0} vendorBinary={1}.",
                    ExtDeviceFaultExtensionName,
                    _supportsExtDeviceFaultVendorBinary);
            }
            else
            {
                Debug.VulkanWarning("[VulkanDiag] Failed to load {0} extension handle.", ExtDeviceFaultExtensionName);
                _supportsExtDeviceFault = false;
                _supportsExtDeviceFaultVendorBinary = false;
                _supportsDeviceFault = _supportsKhrDeviceFault;
                _supportsDeviceFaultVendorBinary = _supportsKhrDeviceFaultVendorBinary;
            }
        }

        if (enabledExtensions.Contains(NvDeviceDiagnosticCheckpointsExtensionName) && _supportsNvDiagnosticCheckpoints)
        {
            if (Api!.TryGetDeviceExtension(instance, device, out _nvDeviceDiagnosticCheckpoints))
            {
                Debug.Vulkan("[VulkanDiag] {0} loaded successfully.", NvDeviceDiagnosticCheckpointsExtensionName);
            }
            else
            {
                Debug.VulkanWarning("[VulkanDiag] Failed to load {0} extension handle.", NvDeviceDiagnosticCheckpointsExtensionName);
                _supportsNvDiagnosticCheckpoints = false;
            }
        }

        if (_supportsNvCopyMemoryIndirect && !_supportsBufferDeviceAddress)
        {
            _supportsNvCopyMemoryIndirect = false;
            _nvCopyMemoryIndirectSupportedQueues = 0;
        }

        // Resolve only after core/extension command tables have finalized the effective
        // dynamic-rendering capability. Resolving earlier could cache legacy mode before
        // VK_KHR_dynamic_rendering command loading completed.
        ResolveRenderTargetMode();
        Debug.Vulkan(
            "[Vulkan] Render target mode: requested={0} resolved={1} dynamicRenderingFeature={2}. Override with {3}=Auto|DynamicRendering|LegacyRenderPass.",
            _requestedRenderTargetMode,
            UseDynamicRenderingRenderTargets ? "DynamicRendering" : "LegacyRenderPass",
            _supportsDynamicRendering,
            VulkanRenderTargetModeEnvVar);

        CreateVulkanPipelineCache();

        RuntimeEngine.Rendering.State.HasVulkanMemoryDecompression = SupportsNvMemoryDecompression;
        RuntimeEngine.Rendering.State.HasVulkanCopyMemoryIndirect = SupportsNvCopyMemoryIndirect;
        RuntimeEngine.Rendering.State.HasVulkanRtxIo = SupportsNvMemoryDecompression || SupportsNvCopyMemoryIndirect;
        RuntimeEngine.Rendering.State.HasVulkanDepthClipControl = SupportsDepthClipControl;

        if (descriptorIndexingExtensionLoaded && _supportsDescriptorIndexing)
            Debug.Vulkan("[Vulkan] VK_EXT_descriptor_indexing enabled for descriptor update-after-bind support.");
    }

    private void LogVulkanDiagnosticDeviceCapabilities(
        bool khrDeviceFaultExtensionAvailable,
        bool khrDeviceFaultExtensionEnabled,
        bool khrDeviceFaultFeatureSupported,
        bool khrDeviceFaultVendorBinaryFeatureSupported,
        bool khrDeviceFaultReportMaskedFeatureSupported,
        bool khrDeviceFaultDeviceLostOnMaskedFeatureSupported,
        uint khrDeviceFaultMaxReportCount,
        bool extDeviceFaultExtensionAvailable,
        bool extDeviceFaultExtensionEnabled,
        bool extDeviceFaultFeatureSupported,
        bool extDeviceFaultVendorBinaryFeatureSupported,
        bool deviceAddressBindingReportExtensionAvailable,
        bool deviceAddressBindingReportExtensionEnabled,
        bool deviceAddressBindingReportFeatureSupported,
        bool nvDiagnosticCheckpointsExtensionAvailable,
        bool nvDiagnosticCheckpointsExtensionEnabled,
        bool nvDiagnosticsConfigExtensionAvailable,
        bool nvDiagnosticsConfigExtensionEnabled,
        bool nvDiagnosticsConfigFeatureSupported)
    {
        Debug.Vulkan(
            "[VulkanDiag] DeviceFault requested={0} khrAvailable={1} khrEnabled={2} khrFeature={3} khrVendorBinary={4} khrReportMasked={5} khrDeviceLostOnMasked={6} khrMaxReports={7} khrCommandTable={8} extAvailable={9} extEnabled={10} extFeature={11} extVendorBinary={12} extCommandTable={13} activePath={14}",
            _diagnosticOptions.RequestDeviceFault,
            khrDeviceFaultExtensionAvailable,
            khrDeviceFaultExtensionEnabled,
            khrDeviceFaultFeatureSupported,
            khrDeviceFaultVendorBinaryFeatureSupported,
            khrDeviceFaultReportMaskedFeatureSupported,
            khrDeviceFaultDeviceLostOnMaskedFeatureSupported,
            khrDeviceFaultMaxReportCount,
            _vkGetDeviceFaultReportsKHR is not null,
            extDeviceFaultExtensionAvailable,
            extDeviceFaultExtensionEnabled,
            extDeviceFaultFeatureSupported,
            extDeviceFaultVendorBinaryFeatureSupported,
            _extDeviceFault is not null,
            _deviceFaultUsingKhr ? "KHR" : _extDeviceFault is not null ? "EXT" : "unavailable");

        Debug.Vulkan(
            "[VulkanDiag] AddressBindingReport requested={0} available={1} enabled={2} feature={3}",
            _diagnosticOptions.RequestDeviceAddressBindingReport,
            deviceAddressBindingReportExtensionAvailable,
            deviceAddressBindingReportExtensionEnabled,
            deviceAddressBindingReportFeatureSupported);

        Debug.Vulkan(
            "[VulkanDiag] NvDiagnosticCheckpoints requested={0} available={1} enabled={2} commandTable={3}",
            _diagnosticOptions.RequestNvDiagnosticCheckpoints,
            nvDiagnosticCheckpointsExtensionAvailable,
            nvDiagnosticCheckpointsExtensionEnabled,
            _nvDeviceDiagnosticCheckpoints is not null);

        Debug.Vulkan(
            "[VulkanDiag] NvDiagnosticsConfig requested={0} available={1} enabled={2} feature={3}",
            _diagnosticOptions.RequestNvDiagnosticsConfig,
            nvDiagnosticsConfigExtensionAvailable,
            nvDiagnosticsConfigExtensionEnabled,
            nvDiagnosticsConfigFeatureSupported);

        Debug.Vulkan(
            "[VulkanDiag] VendorCrashHooks amdIntelRuntimeDependency=none amdIntelNativeHook={0} fallbackArtifacts={1}",
            "unavailable",
            "deviceFault,addressBindingReport,validationSummary");

        if (_diagnosticOptions.RequestDeviceFault && !_supportsDeviceFault)
            Debug.VulkanWarning("[VulkanDiag] Device-fault reports will be unavailable for this run.");
        if (_diagnosticOptions.RequestNvDiagnosticCheckpoints && !SupportsNvDiagnosticCheckpoints)
            Debug.VulkanWarning("[VulkanDiag] NV diagnostic checkpoints will be unavailable for this run.");
        if (_diagnosticOptions.RequestNvDiagnosticsConfig && !SupportsNvDiagnosticsConfig)
            Debug.VulkanWarning("[VulkanDiag] NV diagnostics config will be unavailable for this run.");
    }

    private void ReportLayeredShadowCapabilities(
        bool enableMultiViewport,
        bool enableShaderOutputViewportIndex,
        bool enableShaderOutputLayer)
    {
        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        int maxViewports = enableMultiViewport
            ? System.Math.Max(1, unchecked((int)System.Math.Min(properties.Limits.MaxViewports, (uint)int.MaxValue)))
            : 1;

        // These OpenGL-named flags are the renderer-wide shadow-planner contract.
        // Vulkan reports equivalent capabilities from multiViewport and shader-output
        // ViewportIndex/Layer feature bits so shared light code can choose atlas paths.
        RuntimeEngine.Rendering.State.SupportsOpenGLViewportArray = enableMultiViewport;
        RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray = enableMultiViewport;
        RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex =
            enableMultiViewport && enableShaderOutputViewportIndex;
        RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex =
            enableMultiViewport && enableShaderOutputViewportIndex && _supportsGeometryShader;
        RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering = enableShaderOutputLayer;
        RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderLayeredRendering =
            enableShaderOutputLayer && _supportsGeometryShader;
        RuntimeEngine.Rendering.State.SupportsOpenGLLayeredFramebuffers = enableShaderOutputLayer;
        RuntimeEngine.Rendering.State.MaxOpenGLViewports = maxViewports;

        Debug.Vulkan(
            "[Vulkan] Layered shadow planner capabilities: multiViewport={0}, maxViewports={1}, shaderOutputViewportIndex={2}, shaderOutputLayer={3}, geometryShader={4}.",
            enableMultiViewport,
            maxViewports,
            enableShaderOutputViewportIndex,
            enableShaderOutputLayer,
            _supportsGeometryShader);
    }

    private void ValidateExplicitModernBackendRequests(
        bool vulkan13PromotedToCore,
        bool vulkan14PromotedToCore,
        bool dynamicRenderingEnabled,
        bool dynamicRenderingLocalReadEnabled,
        bool maintenance4Enabled,
        bool maintenance5Enabled,
        bool synchronization2Enabled,
        bool timelineSemaphoreEnabled,
        bool descriptorIndexingEnabled,
        bool bufferDeviceAddressEnabled,
        bool drawIndirectCountEnabled,
        bool descriptorHeapExtensionAvailable,
        bool descriptorHeapDependenciesReady,
        bool shaderObjectFeatureSupported,
        bool fragmentShadingRateSupported,
        bool fragmentDensityMapSupported,
        bool accelerationStructureSupported,
        bool rayTracingPipelineSupported,
        bool rayQuerySupported)
    {
        bool productionTierReady =
            vulkan13PromotedToCore &&
            dynamicRenderingEnabled &&
            synchronization2Enabled &&
            timelineSemaphoreEnabled &&
            descriptorIndexingEnabled &&
            bufferDeviceAddressEnabled &&
            drawIndirectCountEnabled &&
            maintenance4Enabled;
        bool vulkan14OptInTierReady =
            vulkan14PromotedToCore &&
            dynamicRenderingLocalReadEnabled &&
            maintenance5Enabled;
        bool vulkan14ExperimentalTierReady =
            vulkan14OptInTierReady &&
            descriptorHeapExtensionAvailable &&
            descriptorHeapDependenciesReady &&
            _descriptorHeapNativeApiAvailable &&
            shaderObjectFeatureSupported;

        if (VulkanFeatureProfile.TryGetCapabilityTierEnvOverride(out EVulkanCapabilityTier tier))
        {
            if (tier == EVulkanCapabilityTier.Vulkan13Production && !productionTierReady)
            {
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.CapabilityTierEnvVar,
                    tier.ToString(),
                    "Vulkan 1.3 production tier requires dynamic rendering, Sync2, timeline semaphores, descriptor indexing, buffer device address, draw indirect count, and maintenance4.");
            }

            if (tier == EVulkanCapabilityTier.Vulkan14OptInBaseline && !vulkan14OptInTierReady)
            {
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.CapabilityTierEnvVar,
                    tier.ToString(),
                    "Vulkan 1.4 opt-in tier requires Vulkan 1.4, dynamic rendering local read, and maintenance5.");
            }

            if (tier == EVulkanCapabilityTier.Vulkan14Experimental && !vulkan14ExperimentalTierReady)
            {
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.CapabilityTierEnvVar,
                    tier.ToString(),
                    "Vulkan 1.4 experimental tier requires the opt-in tier plus descriptor heap binding support and shader-object capability.");
            }
        }

        if (VulkanFeatureProfile.TryGetDescriptorBackendEnvOverride(out EVulkanDescriptorBackend descriptorBackend))
        {
            if (descriptorBackend == EVulkanDescriptorBackend.DescriptorIndexing && !descriptorIndexingEnabled)
            {
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.DescriptorBackendEnvVar,
                    descriptorBackend.ToString(),
                    "Descriptor indexing was explicitly requested, but the descriptor indexing feature set is unavailable.");
            }

            if (descriptorBackend == EVulkanDescriptorBackend.DescriptorHeap)
            {
                string reason = !descriptorHeapExtensionAvailable
                    ? "VK_EXT_descriptor_heap is not exposed by the selected physical device."
                    : !descriptorHeapDependenciesReady
                        ? "VK_EXT_descriptor_heap dependencies are incomplete; the path needs Vulkan 1.4 or maintenance5/extended flags plus buffer device address/Vulkan 1.2 and shader untyped pointers support."
                        : !SupportsDescriptorHeap
                            ? "VK_EXT_descriptor_heap is exposed, but native entry points, feature enablement, or heap storage initialization failed."
                            : _activeDescriptorBackend != EVulkanDescriptorBackend.DescriptorHeap
                                ? _descriptorBackendFallbackReason
                                : string.Empty;

                if (string.IsNullOrWhiteSpace(reason))
                    return;

                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.DescriptorBackendEnvVar,
                    descriptorBackend.ToString(),
                    reason);
            }
        }

        if (VulkanFeatureProfile.TryGetProgramBindingBackendEnvOverride(out EVulkanProgramBindingBackend programBindingBackend) &&
            programBindingBackend == EVulkanProgramBindingBackend.ShaderObjects)
        {
            string reason = shaderObjectFeatureSupported
                ? "VK_EXT_shader_object is available, but the renderer shader-object program-binding backend is not implemented yet."
                : "VK_EXT_shader_object is unavailable or its shaderObject feature bit is false.";
            ThrowExplicitCapabilityMissing(
                VulkanFeatureProfile.ProgramBindingBackendEnvVar,
                programBindingBackend.ToString(),
                reason);
        }

        if (VulkanFeatureProfile.TryGetFoveationBackendEnvOverride(out EVulkanFoveationBackend foveationBackend))
        {
            if (foveationBackend == EVulkanFoveationBackend.FragmentShadingRate)
            {
                string reason = fragmentShadingRateSupported
                    ? "Fragment shading rate is available, but the Vulkan VRS/foveation backend is not implemented yet."
                    : "VK_KHR_fragment_shading_rate is unavailable or no fragment shading-rate feature bit is supported.";
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.FoveationBackendEnvVar,
                    foveationBackend.ToString(),
                    reason);
            }

            if (foveationBackend == EVulkanFoveationBackend.FragmentDensityMap)
            {
                string reason = fragmentDensityMapSupported
                    ? "Fragment density map is available, but the Vulkan density-map foveation backend is not implemented yet."
                    : "VK_EXT_fragment_density_map is unavailable or its feature bit is false.";
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.FoveationBackendEnvVar,
                    foveationBackend.ToString(),
                    reason);
            }
        }

        if (VulkanFeatureProfile.TryGetRayTracingBackendEnvOverride(out EVulkanRayTracingBackend rayTracingBackend))
        {
            if (rayTracingBackend == EVulkanRayTracingBackend.RayTracingPipeline)
            {
                string reason = accelerationStructureSupported && rayTracingPipelineSupported
                    ? "KHR ray tracing pipeline support is available, but the Vulkan ray-tracing backend is not implemented yet."
                    : "KHR ray tracing pipeline support requires acceleration structures, ray tracing pipeline, and deferred host operations.";
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.RayTracingBackendEnvVar,
                    rayTracingBackend.ToString(),
                    reason);
            }

            if (rayTracingBackend == EVulkanRayTracingBackend.RayQuery)
            {
                string reason = accelerationStructureSupported && rayQuerySupported
                    ? "Ray query support is available, but the Vulkan ray-query backend is not implemented yet."
                    : "KHR ray query support requires acceleration structures and the rayQuery feature bit.";
                ThrowExplicitCapabilityMissing(
                    VulkanFeatureProfile.RayTracingBackendEnvVar,
                    rayTracingBackend.ToString(),
                    reason);
            }
        }
    }

    private static void ThrowExplicitCapabilityMissing(string environmentVariable, string value, string reason)
    {
        Debug.VulkanWarning(
            "[Vulkan] Capability.ExplicitRequest state=explicitly-required-missing env={0} value={1} reason='{2}'",
            environmentVariable,
            value,
            reason);
        throw new InvalidOperationException(
            $"Vulkan capability request {environmentVariable}={value} cannot be satisfied: {reason}");
    }

    private void LogStartupCapabilitySnapshot()
    {
        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        Api!.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);

        string apiVersion = FormatVulkanApiVersion(properties.ApiVersion);
        var availableExtensions = new HashSet<string>(_availableDeviceExtensions, StringComparer.Ordinal);
        var enabledExtensions = new HashSet<string>(_enabledDeviceExtensions, StringComparer.Ordinal);
        bool HasExtension(string extensionName) => availableExtensions.Contains(extensionName);
        bool HasEnabledExtension(string extensionName) => enabledExtensions.Contains(extensionName);

        EVulkanDescriptorBackend requestedDescriptorBackend = VulkanFeatureProfile.RequestedDescriptorBackend;
        EVulkanDescriptorBackend descriptorBackend = _activeDescriptorBackend;
        EVulkanProgramBindingBackend programBindingBackend = VulkanFeatureProfile.RequestedProgramBindingBackend;
        EVulkanFoveationBackend foveationBackend = VulkanFeatureProfile.RequestedFoveationBackend;
        EVulkanRayTracingBackend rayTracingBackend = VulkanFeatureProfile.RequestedRayTracingBackend;

        bool productionTierReady =
            SupportsDynamicRendering &&
            SupportsSynchronization2 &&
            _supportsTimelineSemaphores &&
            SupportsMaintenance4 &&
            SupportsDescriptorIndexing &&
            SupportsBufferDeviceAddress &&
            _supportsDrawIndirectCount;
        bool vulkan14OptInTierReady =
            SupportsVulkan14 &&
            SupportsDynamicRenderingLocalRead &&
            SupportsMaintenance5;
        bool vulkan14ExperimentalTierReady =
            vulkan14OptInTierReady &&
            HasExtension(VulkanDescriptorHeapExt.ExtensionName) &&
            _descriptorHeapNativeApiAvailable &&
            SupportsShaderObject;

        Debug.Vulkan(
            "[Vulkan] Capability.Snapshot apiVersion={0} requestedTier={1} requestedDescriptorBackend={2} activeDescriptorBackend={3} requestedProgramBindingBackend={4} requestedFoveationBackend={5} requestedRayTracingBackend={6}",
            apiVersion,
            VulkanFeatureProfile.RequestedCapabilityTier,
            requestedDescriptorBackend,
            descriptorBackend,
            programBindingBackend,
            foveationBackend,
            rayTracingBackend);

        foreach (string extensionName in ReportedModernCapabilityExtensionNames)
        {
            Debug.Vulkan(
                "[Vulkan] Capability.Extension name={0} available={1} enabled={2}",
                extensionName,
                HasExtension(extensionName),
                HasEnabledExtension(extensionName));
        }

        LogCapability(
            "Vulkan13ProductionTier",
            CapabilityState(true, productionTierReady, productionTierReady),
            apiVersion,
            "Vulkan 1.3",
            "dynamicRendering+sync2+timelineSemaphore+descriptorIndexing+bufferDeviceAddress+drawIndirectCount+maintenance4",
            $"renderTarget={_requestedRenderTargetMode}->{(UseDynamicRenderingRenderTargets ? "DynamicRendering" : "LegacyRenderPass")};sync={RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.SyncBackend}->{_activeSynchronizationBackend};descriptor={descriptorBackend}",
            $"ready={productionTierReady}",
            productionTierReady ? string.Empty : "Production tier incomplete; see individual capability rows.");

        LogCapability(
            "Vulkan14OptInTier",
            CapabilityState(SupportsVulkan14, vulkan14OptInTierReady, false),
            apiVersion,
            "Vulkan 1.4",
            "dynamicRenderingLocalRead+maintenance5",
            VulkanFeatureProfile.RequestedCapabilityTier.ToString(),
            $"ready={vulkan14OptInTierReady}",
            vulkan14OptInTierReady ? string.Empty : "Optional Vulkan 1.4 tier is not fully available.");

        LogCapability(
            "Vulkan14ExperimentalTier",
            CapabilityState(SupportsVulkan14, vulkan14ExperimentalTierReady, false),
            apiVersion,
            "Vulkan 1.4 + VK_EXT_descriptor_heap + VK_EXT_shader_object",
            "descriptorHeap+shaderObject",
            VulkanFeatureProfile.RequestedCapabilityTier.ToString(),
            $"ready={vulkan14ExperimentalTierReady};descriptorHeapNativeApi={_descriptorHeapNativeApiAvailable};descriptorHeapStorage={_descriptorHeapStorageReady}",
            vulkan14ExperimentalTierReady ? string.Empty : "Experimental tier remains disabled until descriptor heap native API/storage and shader-object backend exist.");

        LogCapability(
            "DynamicRendering",
            CapabilityState(SupportsDynamicRendering, SupportsDynamicRendering, UseDynamicRenderingRenderTargets),
            apiVersion,
            "VK_KHR_dynamic_rendering / Vulkan 1.3",
            "dynamicRendering",
            $"{_requestedRenderTargetMode}->{(UseDynamicRenderingRenderTargets ? "DynamicRendering" : "LegacyRenderPass")}",
            $"extensionEnabled={HasEnabledExtension("VK_KHR_dynamic_rendering")}",
            UseDynamicRenderingRenderTargets ? string.Empty : "Legacy render-pass target mode selected or dynamic rendering unavailable.");

        LogCapability(
            "DynamicRenderingLocalRead",
            CapabilityState(
                HasExtension("VK_KHR_dynamic_rendering_local_read") || SupportsVulkan14,
                SupportsDynamicRenderingLocalRead,
                false),
            apiVersion,
            "VK_KHR_dynamic_rendering_local_read / Vulkan 1.4",
            "dynamicRenderingLocalRead",
            "OptionalPrototype",
            $"storageResources={SupportsDynamicRenderingLocalReadStorageResources};singleSampledColor={SupportsDynamicRenderingLocalReadColorAttachments};depthStencil={SupportsDynamicRenderingLocalReadDepthStencilAttachments};multisampled={SupportsDynamicRenderingLocalReadMultisampledAttachments}",
            SupportsDynamicRenderingLocalRead ? "No pass has opted into local-read barriers yet." : "Local read remains optional until Vulkan 1.4 tier is required.");

        LogCapability(
            "Synchronization2",
            CapabilityState(
                HasExtension("VK_KHR_synchronization2") || IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 3u),
                SupportsSynchronization2,
                _activeSynchronizationBackend == EVulkanSynchronizationBackend.Sync2),
            apiVersion,
            "VK_KHR_synchronization2 / Vulkan 1.3",
            "synchronization2",
            $"{RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.SyncBackend}->{_activeSynchronizationBackend}",
            $"featureSupported={_supportsSynchronization2Feature}",
            _activeSynchronizationBackend == EVulkanSynchronizationBackend.Sync2 ? string.Empty : "Legacy synchronization backend selected or Sync2 unavailable.");

        LogCapability(
            "TimelineSemaphore",
            CapabilityState(_supportsTimelineSemaphores, _supportsTimelineSemaphores, _supportsTimelineSemaphores),
            apiVersion,
            "Vulkan 1.2 timelineSemaphore",
            "timelineSemaphore",
            "FramePacing",
            "required=True",
            _supportsTimelineSemaphores ? string.Empty : "Renderer timeline synchronization requires timeline semaphores.");

        LogCapability(
            "DescriptorIndexing",
            CapabilityState(
                HasExtension("VK_EXT_descriptor_indexing") || IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 2u),
                SupportsDescriptorIndexing,
                SupportsDescriptorIndexing && descriptorBackend == EVulkanDescriptorBackend.DescriptorIndexing),
            apiVersion,
            "VK_EXT_descriptor_indexing / Vulkan 1.2",
            "descriptorIndexing+runtimeDescriptorArray+partiallyBound+updateAfterBind",
            descriptorBackend.ToString(),
            $"runtimeArray={_supportsRuntimeDescriptorArray};partiallyBound={_supportsDescriptorBindingPartiallyBound};updateAfterBind={_supportsDescriptorBindingUpdateAfterBind};storageImageUpdateAfterBind={_supportsDescriptorBindingStorageImageUpdateAfterBind}",
            SupportsDescriptorIndexing ? string.Empty : "Descriptor sets remain on the non-indexed path.");

        VulkanBindlessMaterialCapability bindlessMaterialCapability = RefreshBindlessMaterialCapability();
        LogCapability(
            "BindlessMaterialTextures",
            CapabilityState(
                SupportsDescriptorIndexing,
                bindlessMaterialCapability.Tier >= EVulkanBindlessMaterialCapabilityTier.DescriptorIndexingReady,
                bindlessMaterialCapability.DrawPathReady),
            apiVersion,
            "VK_EXT_descriptor_indexing / Vulkan 1.2",
            "descriptorIndexing",
            bindlessMaterialCapability.Mode.ToString(),
            $"tier={bindlessMaterialCapability.Tier};capacity={bindlessMaterialCapability.DescriptorCapacity};tableReady={bindlessMaterialCapability.GlobalDescriptorTableReady};shaderReady={bindlessMaterialCapability.ShaderReady};drawPathReady={bindlessMaterialCapability.DrawPathReady}",
            bindlessMaterialCapability.Reason);

        LogCapability(
            "DescriptorHeap",
            CapabilityState(HasExtension(VulkanDescriptorHeapExt.ExtensionName), SupportsDescriptorHeap, _activeDescriptorBackend == EVulkanDescriptorBackend.DescriptorHeap),
            apiVersion,
            "VK_EXT_descriptor_heap",
            "descriptorHeap",
            $"{requestedDescriptorBackend}->{descriptorBackend}",
            $"feature={_descriptorHeapFeatureSupported};captureReplay={_descriptorHeapCaptureReplaySupported};nativeApi={_descriptorHeapNativeApiAvailable};storage={_descriptorHeapStorageReady};shaderUntypedPointers={_descriptorHeapShaderUntypedPointersAvailable};samplerHeapBytes={DescriptorHeapSamplerCapacityBytes};resourceHeapBytes={DescriptorHeapResourceCapacityBytes};samplerDescriptorSize={_descriptorHeapProperties.SamplerDescriptorSize};imageDescriptorSize={_descriptorHeapProperties.ImageDescriptorSize};bufferDescriptorSize={_descriptorHeapProperties.BufferDescriptorSize};samplerWrites={_descriptorHeapSamplerWriteCount};resourceWrites={_descriptorHeapResourceWriteCount};samplerBinds={_descriptorHeapSamplerBindCount};resourceBinds={_descriptorHeapResourceBindCount}",
            HasExtension(VulkanDescriptorHeapExt.ExtensionName)
                ? _descriptorBackendFallbackReason
                : "VK_EXT_descriptor_heap is not exposed by the selected physical device.");

        LogCapability(
            "DescriptorBuffer",
            CapabilityState(HasExtension("VK_EXT_descriptor_buffer"), false, false),
            apiVersion,
            "VK_EXT_descriptor_buffer",
            "descriptorBuffer",
            "NotTargetBackend",
            "deprecatedBy=VK_EXT_descriptor_heap",
            "Descriptor buffer is intentionally not the long-term modernization backend.");

        LogCapability(
            "BufferDeviceAddress",
            CapabilityState(
                HasExtension("VK_KHR_buffer_device_address") || IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 2u),
                SupportsBufferDeviceAddress,
                SupportsBufferDeviceAddress && (SupportsNvCopyMemoryIndirect || VulkanFeatureProfile.EnableBindlessMaterialTable)),
            apiVersion,
            "VK_KHR_buffer_device_address / Vulkan 1.2",
            "bufferDeviceAddress",
            VulkanFeatureProfile.ActiveGeometryFetchMode.ToString(),
            $"nvCopyMemoryIndirect={SupportsNvCopyMemoryIndirect};bindlessMaterial={VulkanFeatureProfile.EnableBindlessMaterialTable}",
            SupportsBufferDeviceAddress ? string.Empty : "Buffer-device-address consumers remain disabled.");

        LogCapability(
            "ShaderObject",
            CapabilityState(HasExtension("VK_EXT_shader_object"), SupportsShaderObject, false),
            apiVersion,
            "VK_EXT_shader_object",
            "shaderObject",
            $"{programBindingBackend}->PipelineObjects",
            $"shaderBinaryVersion={_shaderObjectProperties.ShaderBinaryVersion}",
            SupportsShaderObject ? "Shader-object backend is not implemented yet; pipeline objects remain active." : "VK_EXT_shader_object unavailable or feature bit is false.");

        LogCapability(
            "GraphicsPipelineLibrary",
            CapabilityState(HasExtension("VK_EXT_graphics_pipeline_library"), SupportsGraphicsPipelineLibrary, SupportsGraphicsPipelineLibrary),
            apiVersion,
            "VK_KHR_pipeline_library + VK_EXT_graphics_pipeline_library",
            "graphicsPipelineLibrary",
            "PipelineObjects",
            $"dependencyEnabled={HasEnabledExtension("VK_KHR_pipeline_library")}",
            SupportsGraphicsPipelineLibrary ? string.Empty : "Monolithic pipeline fallback remains available.");

        LogCapability(
            "FragmentShadingRate",
            CapabilityState(HasExtension("VK_KHR_fragment_shading_rate"), SupportsVulkanFragmentShadingRate, false),
            apiVersion,
            "VK_KHR_fragment_shading_rate",
            "pipelineFragmentShadingRate|primitiveFragmentShadingRate|attachmentFragmentShadingRate",
            $"{foveationBackend}->Off",
            $"attachment={SupportsVulkanFragmentShadingRateAttachment}",
            SupportsVulkanFragmentShadingRate ? "VRS/foveation backend is not implemented yet." : "Fragment shading rate unavailable.");

        LogCapability(
            "FragmentDensityMap",
            CapabilityState(HasExtension("VK_EXT_fragment_density_map"), SupportsVulkanFragmentDensityMap, false),
            apiVersion,
            "VK_EXT_fragment_density_map",
            "fragmentDensityMap",
            $"{foveationBackend}->Off",
            $"dynamic={SupportsVulkanFragmentDensityMapDynamic}",
            SupportsVulkanFragmentDensityMap ? "Density-map foveation backend is not implemented yet." : "Fragment density map unavailable.");

        LogCapability(
            "Multiview",
            CapabilityState(
                HasExtension("VK_KHR_multiview") || IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 1u),
                RuntimeEngine.Rendering.State.HasVulkanMultiView,
                RuntimeEngine.Rendering.State.HasVulkanMultiView),
            apiVersion,
            "VK_KHR_multiview / Vulkan 1.1",
            "multiview",
            "StereoTargets",
            $"enabled={RuntimeEngine.Rendering.State.HasVulkanMultiView}",
            RuntimeEngine.Rendering.State.HasVulkanMultiView ? string.Empty : "Stereo single-pass multiview remains disabled.");

        LogCapability(
            "MeshShaderEXT",
            CapabilityState(HasExtension("VK_EXT_mesh_shader"), SupportsVulkanMeshTaskIndirectCount, false),
            apiVersion,
            "VK_EXT_mesh_shader",
            "taskShader+meshShader",
            "MeshletDispatch",
            $"task={_supportsVulkanTaskShaderFeature};mesh={_supportsVulkanMeshShaderFeature}",
            SupportsVulkanMeshTaskIndirectCount ? "Mesh/task shader capability loaded; production meshlet dispatch remains profile-gated." : "Mesh/task shader path unavailable.");

        LogCapability(
            "DrawIndirectCount",
            CapabilityState(
                HasExtension("VK_KHR_draw_indirect_count") || IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 2u),
                _supportsDrawIndirectCount,
                _supportsDrawIndirectCount),
            apiVersion,
            "VK_KHR_draw_indirect_count / Vulkan 1.2",
            "drawIndirectCount",
            "GpuDrivenIndirect",
            $"extensionLoaded={_khrDrawIndirectCount is not null}",
            _supportsDrawIndirectCount ? string.Empty : "Multi-draw indirect count falls back to non-count indirect path.");

        LogCapability(
            "ExternalMemorySemaphore",
            CapabilityState(
                HasExtension("VK_KHR_external_memory") || HasExtension("VK_KHR_external_semaphore"),
                SupportsExternalMemoryWin32 && SupportsExternalSemaphoreWin32,
                SupportsExternalMemoryWin32 && SupportsExternalSemaphoreWin32),
            apiVersion,
            "VK_KHR_external_memory + VK_KHR_external_semaphore + Win32 variants",
            "externalMemoryWin32+externalSemaphoreWin32",
            "Interop",
            $"memoryWin32={SupportsExternalMemoryWin32};semaphoreWin32={SupportsExternalSemaphoreWin32}",
            SupportsExternalMemoryWin32 && SupportsExternalSemaphoreWin32 ? string.Empty : "Interop/upscale/OBS paths requiring external sharing remain disabled.");

        LogCapability(
            "MemoryBudget",
            CapabilityState(HasExtension("VK_EXT_memory_budget"), SupportsMemoryBudget, false),
            apiVersion,
            "VK_EXT_memory_budget",
            "memoryBudget",
            "AllocatorDiagnostics",
            "heapBudget=reported-when-enabled",
            SupportsMemoryBudget ? "Allocator residency policy has not consumed memory budget yet." : "Memory budget extension unavailable or disabled.");

        LogCapability(
            "MemoryPriority",
            CapabilityState(HasExtension("VK_EXT_memory_priority"), SupportsMemoryPriority, false),
            apiVersion,
            "VK_EXT_memory_priority",
            "memoryPriority",
            "AllocatorDiagnostics",
            $"feature={SupportsMemoryPriority}",
            SupportsMemoryPriority ? "Allocator priority policy has not consumed memory priority yet." : "Memory priority extension unavailable or feature bit is false.");

        LogCapability(
            "TransientLazyAttachments",
            CapabilityState(true, SupportsLazyAllocation, SupportsLazyAllocation),
            apiVersion,
            "MemoryPropertyFlags.LazilyAllocatedBit",
            "lazilyAllocatedMemoryType",
            "TransientAttachmentPolicy",
            $"lazyAlloc={SupportsLazyAllocation}",
            SupportsLazyAllocation ? string.Empty : "Transient images fall back to regular device-local memory.");

        LogCapability(
            "RayTracingPipeline",
            CapabilityState(
                HasExtension("VK_KHR_ray_tracing_pipeline"),
                SupportsAccelerationStructure && SupportsRayTracingPipeline,
                false),
            apiVersion,
            "VK_KHR_acceleration_structure + VK_KHR_ray_tracing_pipeline + VK_KHR_deferred_host_operations",
            "accelerationStructure+rayTracingPipeline",
            $"{rayTracingBackend}->Off",
            $"accelerationStructure={SupportsAccelerationStructure};rayTracingPipeline={SupportsRayTracingPipeline}",
            SupportsAccelerationStructure && SupportsRayTracingPipeline ? "Vulkan ray tracing backend is not implemented yet." : "KHR ray tracing pipeline requirements are incomplete.");

        LogCapability(
            "RayQuery",
            CapabilityState(HasExtension("VK_KHR_ray_query"), SupportsAccelerationStructure && SupportsRayQuery, false),
            apiVersion,
            "VK_KHR_ray_query",
            "rayQuery",
            $"{rayTracingBackend}->Off",
            $"accelerationStructure={SupportsAccelerationStructure};rayQuery={SupportsRayQuery}",
            SupportsAccelerationStructure && SupportsRayQuery ? "Vulkan ray-query backend is not implemented yet." : "Ray query requirements are incomplete.");

        LogCapability(
            "DeviceGeneratedCommands",
            CapabilityState(HasExtension("VK_EXT_device_generated_commands"), SupportsDeviceGeneratedCommands, false),
            apiVersion,
            "VK_EXT_device_generated_commands",
            "deviceGeneratedCommands",
            "Deferred",
            $"feature={SupportsDeviceGeneratedCommands}",
            SupportsDeviceGeneratedCommands ? "Deferred until descriptor heap/shader object architecture is stable." : "Device-generated commands unavailable.");

        LogCapability(
            "Maintenance4",
            CapabilityState(
                HasExtension("VK_KHR_maintenance4") || IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 3u),
                SupportsMaintenance4,
                SupportsMaintenance4),
            apiVersion,
            "VK_KHR_maintenance4 / Vulkan 1.3",
            "maintenance4",
            "ProductionTier",
            $"enabled={SupportsMaintenance4}",
            SupportsMaintenance4 ? string.Empty : "Maintenance4 unavailable.");

        LogCapability(
            "Maintenance5",
            CapabilityState(
                HasExtension("VK_KHR_maintenance5") || SupportsVulkan14,
                SupportsMaintenance5,
                false),
            apiVersion,
            "VK_KHR_maintenance5 / Vulkan 1.4",
            "maintenance5",
            "DescriptorHeapDependency",
            $"enabled={SupportsMaintenance5}",
            SupportsMaintenance5 ? "Available for descriptor heap dependency checks." : "Maintenance5 unavailable.");

        LogCapability(
            "ExtendedFlags",
            CapabilityState(HasExtension("VK_KHR_extended_flags"), SupportsExtendedFlags, false),
            apiVersion,
            "VK_KHR_extended_flags",
            "extendedFlags",
            "DescriptorHeapDependency",
            $"enabled={SupportsExtendedFlags}",
            SupportsExtendedFlags ? "Available for descriptor heap dependency checks." : "Extended flags unavailable.");

        LogCapability(
            "ShaderUntypedPointers",
            CapabilityState(HasExtension(VulkanDescriptorHeapExt.ShaderUntypedPointersExtensionName), _descriptorHeapShaderUntypedPointersAvailable, false),
            apiVersion,
            VulkanDescriptorHeapExt.ShaderUntypedPointersExtensionName,
            "shaderUntypedPointers",
            "DescriptorHeapDependency",
            $"available={_descriptorHeapShaderUntypedPointersAvailable}",
            _descriptorHeapShaderUntypedPointersAvailable
                ? "Descriptor heap dependency is present; legacy set/binding mappings do not require enabling it."
                : "Descriptor heap requires shader untyped pointers support.");

        LogCapability(
            "DepthClipViewportLayer",
            CapabilityState(
                HasExtension(VulkanDepthClipControlExt.ExtensionName) || IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 2u),
                SupportsDepthClipControl || RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering,
                SupportsDepthClipControl || RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering),
            apiVersion,
            "VK_EXT_depth_clip_control + VK_EXT_shader_viewport_index_layer",
            "depthClipControl+shaderOutputViewportIndex+shaderOutputLayer",
            "LayeredShadowPlanning",
            $"depthClip={SupportsDepthClipControl};viewportLayer={RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering}",
            SupportsDepthClipControl ? string.Empty : "Depth clip control unavailable; clip-space/layered paths use fallbacks.");

        LogCapability(
            "IndexTypeUint8",
            CapabilityState(
                HasExtension("VK_EXT_index_type_uint8") || HasExtension("VK_KHR_index_type_uint8") || SupportsVulkan14,
                SupportsIndexTypeUint8,
                false),
            apiVersion,
            "VK_EXT_index_type_uint8 / VK_KHR_index_type_uint8 / Vulkan 1.4",
            "indexTypeUint8",
            "IndexBuffers",
            $"enabled={SupportsIndexTypeUint8}",
            SupportsIndexTypeUint8 ? "Byte-sized index buffers are allowed." : "Byte-sized index buffers are skipped.");

        LogCapability(
            "TransformFeedback",
            CapabilityState(HasExtension(ExtTransformFeedback.ExtensionName), SupportsTransformFeedback, SupportsTransformFeedback),
            apiVersion,
            ExtTransformFeedback.ExtensionName,
            "transformFeedback",
            "LegacyParity",
            $"queries={SupportsTransformFeedbackQueries};draw={SupportsTransformFeedbackDraw};geometryStreams={SupportsTransformFeedbackGeometryStreams}",
            SupportsTransformFeedback ? string.Empty : "Transform feedback unavailable.");

        LogCapability(
            "NvidiaDataMovement",
            CapabilityState(
                HasExtension("VK_NV_memory_decompression") || HasExtension("VK_NV_copy_memory_indirect"),
                SupportsNvMemoryDecompression || SupportsNvCopyMemoryIndirect,
                SupportsNvMemoryDecompression || SupportsNvCopyMemoryIndirect),
            apiVersion,
            "VK_NV_memory_decompression + VK_NV_copy_memory_indirect",
            "memoryDecompression+copyMemoryIndirect",
            "RtxIo",
            $"decompression={SupportsNvMemoryDecompression};copyIndirect={SupportsNvCopyMemoryIndirect};methods=0x{NvMemoryDecompressionMethods:X};copyQueues=0x{NvCopyMemoryIndirectSupportedQueues:X}",
            SupportsNvMemoryDecompression || SupportsNvCopyMemoryIndirect ? string.Empty : "NVIDIA accelerated data movement unavailable or disabled.");

        Debug.Vulkan(
            "[Vulkan] Capability.MaxMemoryAllocationCount status=Required supported=True value={0}",
            properties.Limits.MaxMemoryAllocationCount);

        Debug.Vulkan(
            "[Vulkan] Capability.MemoryHeaps status=Required supported=True {0}",
            FormatMemoryHeaps(memoryProperties));

        Debug.Vulkan(
            "[Vulkan] Capability.MemoryTypes status=Required supported=True {0}",
            FormatMemoryTypes(memoryProperties));
    }

    private static EVulkanCapabilityState CapabilityState(
        bool available,
        bool enabled,
        bool active,
        bool explicitlyRequiredMissing = false)
    {
        if (explicitlyRequiredMissing)
            return EVulkanCapabilityState.ExplicitlyRequiredMissing;
        if (active)
            return EVulkanCapabilityState.EnabledActive;
        if (enabled)
            return EVulkanCapabilityState.EnabledUnused;
        if (available)
            return EVulkanCapabilityState.AvailableDisabled;

        return EVulkanCapabilityState.Unavailable;
    }

    private static void LogCapability(
        string name,
        EVulkanCapabilityState state,
        string apiVersion,
        string extensionName,
        string featureBit,
        string runtimeMode,
        string properties,
        string fallbackReason)
        => Debug.Vulkan(
            "[Vulkan] Capability.{0} state={1} apiVersion={2} extension='{3}' feature='{4}' runtimeMode='{5}' properties='{6}' fallback='{7}'",
            name,
            ToCapabilityStateString(state),
            apiVersion,
            extensionName,
            featureBit,
            runtimeMode,
            properties,
            fallbackReason);

    private static string ToCapabilityStateString(EVulkanCapabilityState state)
        => state switch
        {
            EVulkanCapabilityState.Unavailable => "unavailable",
            EVulkanCapabilityState.AvailableDisabled => "available-disabled",
            EVulkanCapabilityState.EnabledUnused => "enabled-unused",
            EVulkanCapabilityState.EnabledActive => "enabled-active",
            EVulkanCapabilityState.ExplicitlyRequiredMissing => "explicitly-required-missing",
            _ => "unavailable",
        };

    private static string FormatMemoryHeaps(PhysicalDeviceMemoryProperties memoryProperties)
    {
        if (memoryProperties.MemoryHeapCount == 0)
            return "none";

        StringBuilder builder = new();
        for (int i = 0; i < memoryProperties.MemoryHeapCount; i++)
        {
            if (builder.Length > 0)
                builder.Append("; ");

            MemoryHeap heap = memoryProperties.MemoryHeaps[i];
            builder.Append($"heap{i}:size={heap.Size},flags={heap.Flags}");
        }

        return builder.ToString();
    }

    private static string FormatMemoryTypes(PhysicalDeviceMemoryProperties memoryProperties)
    {
        if (memoryProperties.MemoryTypeCount == 0)
            return "none";

        StringBuilder builder = new();
        for (int i = 0; i < memoryProperties.MemoryTypeCount; i++)
        {
            if (builder.Length > 0)
                builder.Append("; ");

            MemoryType memoryType = memoryProperties.MemoryTypes[i];
            builder.Append($"type{i}:heap={memoryType.HeapIndex},flags={memoryType.PropertyFlags}");
        }

        return builder.ToString();
    }
}

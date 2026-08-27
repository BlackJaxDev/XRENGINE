using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.NV;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Native physical-device feature probes used while constructing the logical
/// device. These methods mutate only bootstrap-local capability state.
/// </summary>
internal sealed partial class VulkanDeviceContext
{
    private const uint VulkanApiVersion14 = (1u << 22) | (4u << 12);

    internal unsafe void QueryDepthClipControlCapabilities(bool extensionEnabled, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionEnabled)
            return;
        PhysicalDeviceDepthClipControlFeaturesEXTNative features = new()
        {
            SType = VulkanDepthClipControlExt.PhysicalDeviceFeaturesSType,
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.DepthClipControl;
    }

    internal unsafe void QueryNvMemoryDecompressionCapabilities(
        bool extensionEnabled, out bool featureSupported,
        out MemoryDecompressionMethodFlagsNV decompressionMethods, out ulong maxDecompressionIndirectCount)
    {
        featureSupported = false;
        decompressionMethods = 0;
        maxDecompressionIndirectCount = 0;
        if (!extensionEnabled)
            return;
        PhysicalDeviceMemoryDecompressionFeaturesNV features = new() { SType = StructureType.PhysicalDeviceMemoryDecompressionFeaturesNV };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.MemoryDecompression;
        PhysicalDeviceMemoryDecompressionPropertiesNV properties = new() { SType = StructureType.PhysicalDeviceMemoryDecompressionPropertiesNV };
        PhysicalDeviceProperties2 properties2 = new() { SType = StructureType.PhysicalDeviceProperties2, PNext = &properties };
        Api.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);
        decompressionMethods = (MemoryDecompressionMethodFlagsNV)properties.DecompressionMethods;
        maxDecompressionIndirectCount = properties.MaxDecompressionIndirectCount;
    }

    internal unsafe void QueryNvCopyMemoryIndirectCapabilities(
        bool extensionEnabled, out bool featureSupported, out ulong supportedQueues)
    {
        featureSupported = false;
        supportedQueues = 0;
        if (!extensionEnabled)
            return;
        PhysicalDeviceCopyMemoryIndirectFeaturesNV features = new() { SType = StructureType.PhysicalDeviceCopyMemoryIndirectFeaturesNV };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.IndirectCopy;
        PhysicalDeviceCopyMemoryIndirectPropertiesNV properties = new() { SType = StructureType.PhysicalDeviceCopyMemoryIndirectPropertiesNV };
        PhysicalDeviceProperties2 properties2 = new() { SType = StructureType.PhysicalDeviceProperties2, PNext = &properties };
        Api.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);
        supportedQueues = (ulong)properties.SupportedQueues;
    }

    internal unsafe void QueryBufferDeviceAddressCapabilities(out bool featureSupported)
    {
        PhysicalDeviceBufferDeviceAddressFeatures features = new() { SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.BufferDeviceAddress;
    }

    internal static bool IsVulkanApiVersionAtLeast(uint apiVersion, uint major, uint minor)
    {
        if (major == 1u && minor == 4u)
            return apiVersion >= VulkanApiVersion14;
        uint actualMajor = apiVersion >> 22;
        uint actualMinor = (apiVersion >> 12) & 0x3FFu;
        return actualMajor > major || actualMajor == major && actualMinor >= minor;
    }

    internal static string FormatVulkanApiVersion(uint apiVersion)
        => $"{apiVersion >> 22}.{(apiVersion >> 12) & 0x3FFu}.{apiVersion & 0xFFFu}";

    internal unsafe void QueryMaintenance4Capabilities(bool extensionEnabled, out bool featureSupported)
    {
        Api.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties properties);
        PhysicalDeviceMaintenance4Features features = new() { SType = StructureType.PhysicalDeviceMaintenance4Features };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.Maintenance4 && (properties.ApiVersion >= Vk.Version13 || extensionEnabled);
    }

    internal unsafe void QueryDynamicRenderingCapabilities(bool extensionEnabled, out bool featureSupported, out bool promotedToCore)
    {
        Api.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = properties.ApiVersion >= Vk.Version13;
        PhysicalDeviceDynamicRenderingFeatures features = new() { SType = StructureType.PhysicalDeviceDynamicRenderingFeatures };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.DynamicRendering && (promotedToCore || extensionEnabled);
    }

    internal unsafe void QueryDynamicRenderingLocalReadCapabilities(
        bool extensionEnabled, out bool featureSupported, out bool promotedToCore,
        out bool depthStencilAttachmentsSupported, out bool multisampledAttachmentsSupported)
    {
        featureSupported = false;
        depthStencilAttachmentsSupported = false;
        multisampledAttachmentsSupported = false;
        Api.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 4u);
        if (!promotedToCore && !extensionEnabled)
            return;
        if (promotedToCore)
        {
            PhysicalDeviceDynamicRenderingLocalReadFeatures features = new() { SType = StructureType.PhysicalDeviceDynamicRenderingLocalReadFeatures };
            PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
            Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
            featureSupported = features.DynamicRenderingLocalRead;
            PhysicalDeviceVulkan14Properties properties14 = new() { SType = StructureType.PhysicalDeviceVulkan14Properties };
            PhysicalDeviceProperties2 properties2 = new() { SType = StructureType.PhysicalDeviceProperties2, PNext = &properties14 };
            Api.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);
            MutableCapabilities._vulkan14Properties = properties14;
            depthStencilAttachmentsSupported = properties14.DynamicRenderingLocalReadDepthStencilAttachments;
            multisampledAttachmentsSupported = properties14.DynamicRenderingLocalReadMultisampledAttachments;
            return;
        }
        PhysicalDeviceDynamicRenderingLocalReadFeaturesKHR featuresKhr = new() { SType = StructureType.PhysicalDeviceDynamicRenderingLocalReadFeaturesKhr };
        PhysicalDeviceFeatures2 features2Khr = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &featuresKhr };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2Khr);
        featureSupported = featuresKhr.DynamicRenderingLocalRead;
    }

    internal unsafe void QueryMaintenance5Capabilities(bool extensionEnabled, out bool featureSupported, out bool promotedToCore)
    {
        Api.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 4u);
        featureSupported = false;
        if (!promotedToCore && !extensionEnabled)
            return;
        if (promotedToCore)
        {
            PhysicalDeviceMaintenance5Features features = new() { SType = StructureType.PhysicalDeviceMaintenance5Features };
            PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
            Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
            featureSupported = features.Maintenance5;
            return;
        }
        PhysicalDeviceMaintenance5FeaturesKHR featuresKhr = new() { SType = StructureType.PhysicalDeviceMaintenance5FeaturesKhr };
        PhysicalDeviceFeatures2 features2Khr = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &featuresKhr };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2Khr);
        featureSupported = featuresKhr.Maintenance5;
    }

    internal unsafe void QueryShaderObjectCapabilities(bool extensionAvailable, out bool featureSupported, out PhysicalDeviceShaderObjectPropertiesEXT properties)
    {
        featureSupported = false;
        properties = default;
        if (!extensionAvailable)
            return;
        PhysicalDeviceShaderObjectFeaturesEXT features = new() { SType = StructureType.PhysicalDeviceShaderObjectFeaturesExt };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.ShaderObject;
        PhysicalDeviceShaderObjectPropertiesEXT queriedProperties = new() { SType = StructureType.PhysicalDeviceShaderObjectPropertiesExt };
        PhysicalDeviceProperties2 properties2 = new() { SType = StructureType.PhysicalDeviceProperties2, PNext = &queriedProperties };
        Api.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);
        properties = queriedProperties;
    }

    internal unsafe void QueryMemoryPriorityCapabilities(bool extensionAvailable, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionAvailable)
            return;
        PhysicalDeviceMemoryPriorityFeaturesEXT features = new() { SType = StructureType.PhysicalDeviceMemoryPriorityFeaturesExt };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.MemoryPriority;
    }

    internal unsafe void QueryAccelerationStructureCapabilities(bool extensionAvailable, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionAvailable)
            return;
        PhysicalDeviceAccelerationStructureFeaturesKHR features = new() { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.AccelerationStructure;
    }

    internal unsafe void QueryRayTracingPipelineCapabilities(bool extensionAvailable, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionAvailable)
            return;
        PhysicalDeviceRayTracingPipelineFeaturesKHR features = new() { SType = StructureType.PhysicalDeviceRayTracingPipelineFeaturesKhr };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.RayTracingPipeline;
    }

    internal unsafe void QueryRayQueryCapabilities(bool extensionAvailable, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionAvailable)
            return;
        PhysicalDeviceRayQueryFeaturesKHR features = new() { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.RayQuery;
    }

    internal unsafe void QueryDeviceGeneratedCommandsCapabilities(bool extensionAvailable, out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionAvailable)
            return;
        PhysicalDeviceDeviceGeneratedCommandsFeaturesEXT features = new() { SType = StructureType.PhysicalDeviceDeviceGeneratedCommandsFeaturesExt };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.DeviceGeneratedCommands;
    }

    internal unsafe void QueryDeviceFaultCapabilities(
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
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        deviceFaultSupported = features.DeviceFault;
        vendorBinarySupported = features.DeviceFaultVendorBinary;
    }

    internal unsafe void QueryDeviceAddressBindingReportCapabilities(
        bool extensionEnabled,
        out bool reportAddressBindingSupported)
    {
        reportAddressBindingSupported = false;
        if (!extensionEnabled)
            return;

        PhysicalDeviceAddressBindingReportFeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceAddressBindingReportFeaturesExt,
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        reportAddressBindingSupported = features.ReportAddressBinding;
    }

    internal unsafe void QueryNvDiagnosticsConfigCapabilities(
        bool extensionEnabled,
        out bool diagnosticsConfigSupported)
    {
        diagnosticsConfigSupported = false;
        if (!extensionEnabled)
            return;

        PhysicalDeviceDiagnosticsConfigFeaturesNV features = new()
        {
            SType = StructureType.PhysicalDeviceDiagnosticsConfigFeaturesNV,
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        diagnosticsConfigSupported = features.DiagnosticsConfig;
    }

    internal unsafe void QueryShaderDrawParametersCapabilities(out bool featureSupported)
    {
        PhysicalDeviceVulkan11Features features = new()
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.ShaderDrawParameters;
    }

    internal unsafe void QueryVulkan12Capabilities(
        out PhysicalDeviceVulkan12Features vulkan12Features,
        out bool promotedToCore)
    {
        vulkan12Features = new() { SType = StructureType.PhysicalDeviceVulkan12Features };
        Api.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = properties.ApiVersion >= Vk.Version12;
        if (!promotedToCore)
            return;

        PhysicalDeviceVulkan12Features queriedFeatures = new() { SType = StructureType.PhysicalDeviceVulkan12Features };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &queriedFeatures };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        vulkan12Features = queriedFeatures;
    }

    internal unsafe void QueryMultiviewCapabilities(
        bool extensionEnabled,
        out bool featureSupported,
        out bool promotedToCore)
    {
        Api.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties properties);
        promotedToCore = properties.ApiVersion >= Vk.Version11;
        PhysicalDeviceVulkan11Features features = new() { SType = StructureType.PhysicalDeviceVulkan11Features };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.Multiview && (promotedToCore || extensionEnabled);
    }

    internal unsafe void QueryIndexTypeUint8Capabilities(out bool featureSupported)
    {
        PhysicalDeviceIndexTypeUint8FeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceIndexTypeUint8FeaturesExt,
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.IndexTypeUint8;
    }

    internal unsafe void QueryTimelineSemaphoreCapabilities(out bool featureSupported)
    {
        PhysicalDeviceTimelineSemaphoreFeatures features = new()
        {
            SType = StructureType.PhysicalDeviceTimelineSemaphoreFeatures,
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.TimelineSemaphore;
    }

    internal unsafe void QueryMeshShaderCapabilities(
        bool extensionEnabled,
        out bool taskShaderSupported,
        out bool meshShaderSupported,
        out bool meshShaderQueriesSupported,
        out PhysicalDeviceMeshShaderPropertiesEXT properties)
    {
        taskShaderSupported = false;
        meshShaderSupported = false;
        meshShaderQueriesSupported = false;
        properties = default;
        if (!extensionEnabled)
            return;

        PhysicalDeviceMeshShaderFeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt,
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        taskShaderSupported = features.TaskShader;
        meshShaderSupported = features.MeshShader;
        meshShaderQueriesSupported = features.MeshShaderQueries;

        PhysicalDeviceMeshShaderPropertiesEXT queriedProperties = new()
        {
            SType = StructureType.PhysicalDeviceMeshShaderPropertiesExt,
        };
        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &queriedProperties,
        };
        Api.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);
        properties = queriedProperties;
    }

    internal unsafe void QueryGraphicsPipelineLibraryCapabilities(
        bool extensionEnabled,
        out bool featureSupported)
    {
        featureSupported = false;
        if (!extensionEnabled)
            return;

        PhysicalDeviceGraphicsPipelineLibraryFeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceGraphicsPipelineLibraryFeaturesExt,
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.GraphicsPipelineLibrary;
    }

    internal unsafe void QueryTransformFeedbackCapabilities(
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

        PhysicalDeviceTransformFeedbackFeaturesEXT features = new()
        {
            SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.TransformFeedback;
        geometryStreamsSupported = features.GeometryStreams;

        PhysicalDeviceTransformFeedbackPropertiesEXT queriedProperties = new()
        {
            SType = StructureType.PhysicalDeviceTransformFeedbackPropertiesExt,
        };
        PhysicalDeviceProperties2 properties2 = new() { SType = StructureType.PhysicalDeviceProperties2, PNext = &queriedProperties };
        Api.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);
        properties = queriedProperties;
    }

    internal unsafe void QueryFragmentShadingRateCapabilities(
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
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        pipelineFragmentShadingRate = features.PipelineFragmentShadingRate;
        primitiveFragmentShadingRate = features.PrimitiveFragmentShadingRate;
        attachmentFragmentShadingRate = features.AttachmentFragmentShadingRate;
        featureSupported = pipelineFragmentShadingRate || primitiveFragmentShadingRate || attachmentFragmentShadingRate;

        PhysicalDeviceFragmentShadingRatePropertiesKHR queriedProperties = new()
        {
            SType = StructureType.PhysicalDeviceFragmentShadingRatePropertiesKhr,
        };
        PhysicalDeviceProperties2 properties2 = new() { SType = StructureType.PhysicalDeviceProperties2, PNext = &queriedProperties };
        Api.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);
        properties = queriedProperties;
    }

    internal unsafe void QueryFragmentDensityMapCapabilities(
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
        };
        PhysicalDeviceFeatures2 features2 = new() { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        featureSupported = features.FragmentDensityMap;
        dynamicSupported = features.FragmentDensityMapDynamic;
        nonSubsampledImagesSupported = features.FragmentDensityMapNonSubsampledImages;
    }

    internal unsafe void QueryDescriptorIndexingCapabilities()
    {
        MutableCapabilities._supportsRuntimeDescriptorArray = false;
        MutableCapabilities._supportsDescriptorBindingPartiallyBound = false;
        MutableCapabilities._supportsDescriptorBindingSampledImageUpdateAfterBind = false;
        MutableCapabilities._supportsDescriptorBindingUpdateUnusedWhilePending = false;
        MutableCapabilities._supportsDescriptorBindingStorageImageUpdateAfterBind = false;
        MutableCapabilities._supportsDescriptorBindingStorageBufferUpdateAfterBind = false;
        MutableCapabilities._supportsDescriptorBindingUniformBufferUpdateAfterBind = false;
        MutableCapabilities._supportsDescriptorBindingVariableDescriptorCount = false;
        MutableCapabilities._supportsShaderSampledImageArrayNonUniformIndexing = false;

        PhysicalDeviceDescriptorIndexingFeatures features = new()
        {
            SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
        };
        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);

        MutableCapabilities._supportsRuntimeDescriptorArray = features.RuntimeDescriptorArray;
        MutableCapabilities._supportsDescriptorBindingPartiallyBound = features.DescriptorBindingPartiallyBound;
        MutableCapabilities._supportsDescriptorBindingUpdateUnusedWhilePending =
            features.DescriptorBindingUpdateUnusedWhilePending;
        MutableCapabilities._supportsDescriptorBindingSampledImageUpdateAfterBind =
            features.DescriptorBindingSampledImageUpdateAfterBind;
        MutableCapabilities._supportsDescriptorBindingStorageImageUpdateAfterBind = features.DescriptorBindingStorageImageUpdateAfterBind;
        MutableCapabilities._supportsDescriptorBindingStorageBufferUpdateAfterBind =
            features.DescriptorBindingStorageBufferUpdateAfterBind;
        MutableCapabilities._supportsDescriptorBindingUniformBufferUpdateAfterBind =
            features.DescriptorBindingUniformBufferUpdateAfterBind;
        MutableCapabilities._supportsDescriptorBindingVariableDescriptorCount = features.DescriptorBindingVariableDescriptorCount;
        MutableCapabilities._supportsShaderSampledImageArrayNonUniformIndexing = features.ShaderSampledImageArrayNonUniformIndexing;
    }

    internal unsafe void QuerySynchronization2Capabilities()
    {
        MutableCapabilities._supportsSynchronization2Feature = false;
        PhysicalDeviceSynchronization2Features features = new()
        {
            SType = StructureType.PhysicalDeviceSynchronization2Features,
        };
        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &features2);
        MutableCapabilities._supportsSynchronization2Feature = features.Synchronization2;
    }
}

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.NV;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Owns capability values while physical-device probing and logical-device
/// feature negotiation are in progress. Consumers use the immutable snapshot
/// after publication; only bootstrap code mutates this state.
/// </summary>
internal sealed class VulkanDeviceMutableCapabilities
{
    internal bool _supportsSwapchainColorspace;
    internal bool _supportsDrawIndirectCount;
    internal bool _usesCoreDrawIndirectCountCommands;
    internal bool _supportsMultiDrawIndirect;
    internal bool _supportsDrawIndirectFirstInstance;
    internal bool _supportsVulkanTaskShaderFeature;
    internal bool _supportsVulkanMeshShaderFeature;
    internal bool _supportsVulkanMeshTaskIndirectCount;
    internal VulkanMeshShaderCapabilitySnapshot _meshShaderCapabilitySnapshot;
    internal bool _supportsDescriptorIndexing;
    internal bool _supportsRuntimeDescriptorArray;
    internal bool _supportsDescriptorBindingPartiallyBound;
    internal bool _supportsDescriptorBindingSampledImageUpdateAfterBind;
    internal bool _supportsDescriptorBindingUpdateUnusedWhilePending;
    internal bool _supportsDescriptorBindingStorageImageUpdateAfterBind;
    internal bool _supportsDescriptorBindingStorageBufferUpdateAfterBind;
    internal bool _supportsDescriptorBindingUniformBufferUpdateAfterBind;
    internal bool _supportsDescriptorBindingVariableDescriptorCount;
    internal bool _supportsShaderSampledImageArrayNonUniformIndexing;
    internal bool _supportsExternalMemoryWin32;
    internal bool _supportsExternalSemaphoreWin32;
    internal bool _supportsBufferDeviceAddress;
    internal bool _supportsNvMemoryDecompression;
    internal bool _supportsNvCopyMemoryIndirect;
    internal bool _supportsDynamicRendering;
    internal bool _supportsIndexTypeUint8;
    internal bool _supportsSynchronization2;
    internal bool _supportsDepthClipControl;
    internal bool _supportsGraphicsPipelineLibrary;
    internal bool _supportsTransformFeedback;
    internal bool _supportsTransformFeedbackGeometryStreams;
    internal bool _supportsTransformFeedbackQueries;
    internal bool _supportsTransformFeedbackDraw;
    internal bool _supportsHostQueryReset;
    internal bool _supportsVulkanFragmentShadingRate;
    internal bool _supportsVulkanFragmentShadingRateAttachment;
    internal bool _supportsVulkanFragmentDensityMap;
    internal bool _supportsVulkanFragmentDensityMapDynamic;
    internal bool _supportsFragmentStoresAndAtomics;
    internal bool _supportsVertexPipelineStoresAndAtomics;
    internal bool _supportsGeometryShader;
    internal bool _supportsVulkan14;
    internal bool _supportsDynamicRenderingLocalRead;
    internal bool _supportsDynamicRenderingLocalReadStorageResources;
    internal bool _supportsDynamicRenderingLocalReadColorAttachments;
    internal bool _supportsDynamicRenderingLocalReadDepthStencilAttachments;
    internal bool _supportsDynamicRenderingLocalReadMultisampledAttachments;
    internal bool _supportsMaintenance4;
    internal bool _supportsMaintenance5;
    internal bool _supportsExtendedFlags;
    internal bool _supportsDescriptorHeap;
    internal bool _supportsShaderObject;
    internal bool _supportsMemoryBudget;
    internal bool _supportsMemoryPriority;
    internal bool _supportsAccelerationStructure;
    internal bool _supportsRayTracingPipeline;
    internal bool _supportsRayQuery;
    internal bool _supportsDeviceGeneratedCommands;
    internal PhysicalDeviceTransformFeedbackPropertiesEXT _transformFeedbackProperties;
    internal PhysicalDeviceVulkan14Properties _vulkan14Properties;
    internal PhysicalDeviceShaderObjectPropertiesEXT _shaderObjectProperties;
    internal PhysicalDeviceFragmentShadingRatePropertiesKHR _fragmentShadingRateProperties;
    internal MemoryDecompressionMethodFlagsNV _nvMemoryDecompressionMethods;
    internal ulong _nvMaxMemoryDecompressionIndirectCount;
    internal ulong _nvCopyMemoryIndirectSupportedQueues;
    internal bool _supportsAnisotropy;
    internal bool _supportsTimelineSemaphores;
    internal bool _supportsSynchronization2Feature;
    internal bool _supportsDeviceAddressBindingReport;
    internal bool _supportsNvDiagnosticCheckpoints;
    internal bool _supportsNvDiagnosticsConfig;
    internal bool _surfacePresentScalingInstanceExtensionsEnabled;
    internal bool _useDynamicRenderingRenderTargets;
    internal bool SupportsLazyAllocation;
}

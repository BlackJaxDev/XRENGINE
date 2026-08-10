using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>Published device capability projections for renderer-facing feature selection.</summary>
internal sealed partial class VulkanDeviceContext
{
    internal bool SupportsDeviceFault => IsCapabilityEnabled(EVulkanDeviceCapability.DeviceFault, DeviceFaultFacility.SupportsKhrDeviceFault || DeviceFaultFacility.SupportsExtDeviceFault) && ((DeviceFaultFacility.SupportsKhrDeviceFault && DeviceFaultFacility.GetDeviceFaultReportsKhr is not null) || (DeviceFaultFacility.SupportsExtDeviceFault && ExtensionFunctions.ExtDeviceFault is not null));
    internal bool SupportsDeviceAddressBindingReport => IsCapabilityEnabled(EVulkanDeviceCapability.DeviceAddressBindingReport, MutableCapabilities._supportsDeviceAddressBindingReport);
    internal bool SupportsNvDiagnosticCheckpoints => IsCapabilityEnabled(EVulkanDeviceCapability.NvDiagnosticCheckpoints, MutableCapabilities._supportsNvDiagnosticCheckpoints) && ExtensionFunctions.NvDeviceDiagnosticCheckpoints is not null;
    internal bool SupportsNvDiagnosticsConfig => IsCapabilityEnabled(EVulkanDeviceCapability.NvDiagnosticsConfig, MutableCapabilities._supportsNvDiagnosticsConfig);
    internal bool SupportsExternalMemoryWin32 => MutableCapabilities._supportsExternalMemoryWin32 && ExtensionFunctions.KhrExternalMemoryWin32 is not null;
    internal bool SupportsExternalSemaphoreWin32 => MutableCapabilities._supportsExternalSemaphoreWin32 && ExtensionFunctions.KhrExternalSemaphoreWin32 is not null;
    internal bool SupportsBufferDeviceAddress => IsCapabilityEnabled(EVulkanDeviceCapability.BufferDeviceAddress, MutableCapabilities._supportsBufferDeviceAddress);
    internal bool SupportsDynamicRendering => IsCapabilityEnabled(EVulkanDeviceCapability.DynamicRendering, MutableCapabilities._supportsDynamicRendering);
    internal bool SupportsIndexTypeUint8 => IsCapabilityEnabled(EVulkanDeviceCapability.IndexTypeUint8, MutableCapabilities._supportsIndexTypeUint8);
    internal bool SupportsSynchronization2 => IsCapabilityEnabled(EVulkanDeviceCapability.Synchronization2, MutableCapabilities._supportsSynchronization2);
    internal bool SupportsGraphicsPipelineLibrary => IsCapabilityEnabled(EVulkanDeviceCapability.GraphicsPipelineLibrary, MutableCapabilities._supportsGraphicsPipelineLibrary);
    internal bool SupportsTransformFeedback => IsCapabilityEnabled(EVulkanDeviceCapability.TransformFeedback, MutableCapabilities._supportsTransformFeedback) && ExtensionFunctions.ExtTransformFeedback is not null;
    internal bool SupportsTransformFeedbackGeometryStreams => SupportsTransformFeedback && MutableCapabilities._supportsTransformFeedbackGeometryStreams;
    internal bool SupportsTransformFeedbackQueries => SupportsTransformFeedback && MutableCapabilities._supportsTransformFeedbackQueries;
    internal bool SupportsTransformFeedbackDraw => SupportsTransformFeedback && MutableCapabilities._supportsTransformFeedbackDraw;
    internal bool SupportsHostQueryReset => IsCapabilityEnabled(EVulkanDeviceCapability.HostQueryReset, MutableCapabilities._supportsHostQueryReset);
    internal bool SupportsVulkanFragmentShadingRate => IsCapabilityEnabled(EVulkanDeviceCapability.FragmentShadingRate, MutableCapabilities._supportsVulkanFragmentShadingRate);
    internal bool SupportsVulkanFragmentShadingRateAttachment => MutableCapabilities._supportsVulkanFragmentShadingRateAttachment;
    internal PhysicalDeviceFragmentShadingRatePropertiesKHR FragmentShadingRateProperties => MutableCapabilities._fragmentShadingRateProperties;
    internal bool SupportsVulkanFragmentDensityMap => IsCapabilityEnabled(EVulkanDeviceCapability.FragmentDensityMap, MutableCapabilities._supportsVulkanFragmentDensityMap);
    internal bool SupportsVulkanFragmentDensityMapDynamic => MutableCapabilities._supportsVulkanFragmentDensityMapDynamic;
    internal PhysicalDeviceTransformFeedbackPropertiesEXT TransformFeedbackProperties => MutableCapabilities._transformFeedbackProperties;
    internal bool SupportsFragmentStoresAndAtomics => IsCapabilityEnabled(EVulkanDeviceCapability.FragmentStoresAndAtomics, MutableCapabilities._supportsFragmentStoresAndAtomics);
    internal bool SupportsVertexPipelineStoresAndAtomics => IsCapabilityEnabled(EVulkanDeviceCapability.VertexPipelineStoresAndAtomics, MutableCapabilities._supportsVertexPipelineStoresAndAtomics);
    internal bool SupportsGeometryShader => IsCapabilityEnabled(EVulkanDeviceCapability.GeometryShader, MutableCapabilities._supportsGeometryShader);
    internal bool SupportsVulkan14 => IsCapabilityEnabled(EVulkanDeviceCapability.Vulkan14, MutableCapabilities._supportsVulkan14);
    internal bool SupportsDynamicRenderingLocalRead => IsCapabilityEnabled(EVulkanDeviceCapability.DynamicRenderingLocalRead, MutableCapabilities._supportsDynamicRenderingLocalRead);
    internal bool SupportsDynamicRenderingLocalReadStorageResources => SupportsDynamicRenderingLocalRead && MutableCapabilities._supportsDynamicRenderingLocalReadStorageResources;
    internal bool SupportsDynamicRenderingLocalReadColorAttachments => SupportsDynamicRenderingLocalRead && MutableCapabilities._supportsDynamicRenderingLocalReadColorAttachments;
    internal bool SupportsDynamicRenderingLocalReadDepthStencilAttachments => SupportsDynamicRenderingLocalRead && MutableCapabilities._supportsDynamicRenderingLocalReadDepthStencilAttachments;
    internal bool SupportsDynamicRenderingLocalReadMultisampledAttachments => SupportsDynamicRenderingLocalRead && MutableCapabilities._supportsDynamicRenderingLocalReadMultisampledAttachments;
    internal bool SupportsMaintenance4 => IsCapabilityEnabled(EVulkanDeviceCapability.Maintenance4, MutableCapabilities._supportsMaintenance4);
    internal bool SupportsMaintenance5 => IsCapabilityEnabled(EVulkanDeviceCapability.Maintenance5, MutableCapabilities._supportsMaintenance5);
    internal bool SupportsExtendedFlags => MutableCapabilities._supportsExtendedFlags;
    internal bool SupportsShaderObject => IsCapabilityEnabled(EVulkanDeviceCapability.ShaderObject, MutableCapabilities._supportsShaderObject);
    internal bool SupportsMemoryBudget => IsCapabilityEnabled(EVulkanDeviceCapability.MemoryBudget, MutableCapabilities._supportsMemoryBudget);
    internal bool SupportsMemoryPriority => IsCapabilityEnabled(EVulkanDeviceCapability.MemoryPriority, MutableCapabilities._supportsMemoryPriority);
    internal bool SupportsAccelerationStructure => IsCapabilityEnabled(EVulkanDeviceCapability.AccelerationStructure, MutableCapabilities._supportsAccelerationStructure);
    internal bool SupportsRayTracingPipeline => IsCapabilityEnabled(EVulkanDeviceCapability.RayTracingPipeline, MutableCapabilities._supportsRayTracingPipeline);
    internal bool SupportsRayQuery => IsCapabilityEnabled(EVulkanDeviceCapability.RayQuery, MutableCapabilities._supportsRayQuery);
    internal bool SupportsDeviceGeneratedCommands => IsCapabilityEnabled(EVulkanDeviceCapability.DeviceGeneratedCommands, MutableCapabilities._supportsDeviceGeneratedCommands);
    internal MemoryDecompressionMethodFlagsNV NvMemoryDecompressionMethods => MutableCapabilities._nvMemoryDecompressionMethods;
    internal ulong NvMaxMemoryDecompressionIndirectCount => MutableCapabilities._nvMaxMemoryDecompressionIndirectCount;
    internal ulong NvCopyMemoryIndirectSupportedQueues => MutableCapabilities._nvCopyMemoryIndirectSupportedQueues;

    private bool IsCapabilityEnabled(EVulkanDeviceCapability capability, bool bootstrapValue)
        => CapabilitiesPublished ? Capabilities.Supports(capability) : bootstrapValue;
}

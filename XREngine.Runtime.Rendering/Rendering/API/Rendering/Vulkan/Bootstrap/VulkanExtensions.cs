using System;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.NV;
using System.Collections.Generic;
using System.Linq;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        /// <summary>
        /// The KHR_draw_indirect_count extension handle, loaded at device creation if available.
        /// </summary>
        private KhrDrawIndirectCount? _khrDrawIndirectCount;
        private KhrDynamicRendering? _khrDynamicRendering;
        private KhrSynchronization2? _khrSynchronization2;
        private ExtMeshShader? _extMeshShader;
        private ExtTransformFeedback? _extTransformFeedback;
        private KhrExternalMemoryWin32? _khrExternalMemoryWin32;
        private KhrExternalSemaphoreWin32? _khrExternalSemaphoreWin32;

        /// <summary>
        /// Indicates whether VK_KHR_draw_indirect_count extension is supported and loaded.
        /// </summary>
        private bool _supportsDrawIndirectCount;
        private bool _supportsVulkanTaskShaderFeature;
        private bool _supportsVulkanMeshShaderFeature;
        private bool _supportsVulkanMeshTaskIndirectCount;
        private bool _supportsDescriptorIndexing;
        private bool _supportsRuntimeDescriptorArray;
        private bool _supportsDescriptorBindingPartiallyBound;
        private bool _supportsDescriptorBindingUpdateAfterBind;
        private bool _supportsDescriptorBindingStorageImageUpdateAfterBind;
        private bool _supportsExternalMemoryWin32;
        private bool _supportsExternalSemaphoreWin32;
        private NVMemoryDecompression? _nvMemoryDecompression;
        private NVCopyMemoryIndirect? _nvCopyMemoryIndirect;
        private bool _supportsBufferDeviceAddress;
        private bool _supportsNvMemoryDecompression;
        private bool _supportsNvCopyMemoryIndirect;
        private bool _supportsDynamicRendering;
        private bool _supportsIndexTypeUint8;
        private bool _supportsSynchronization2;
        private bool _supportsDepthClipControl;
        private bool _supportsGraphicsPipelineLibrary;
        private bool _supportsTransformFeedback;
        private bool _supportsTransformFeedbackGeometryStreams;
        private bool _supportsTransformFeedbackQueries;
        private bool _supportsTransformFeedbackDraw;
        private bool _supportsHostQueryReset;
        private bool _supportsVulkanFragmentShadingRate;
        private bool _supportsVulkanFragmentShadingRateAttachment;
        private bool _supportsVulkanFragmentDensityMap;
        private bool _supportsVulkanFragmentDensityMapDynamic;
        private bool _supportsFragmentStoresAndAtomics;
        private bool _supportsVertexPipelineStoresAndAtomics;
        private bool _supportsGeometryShader;
        private bool _supportsVulkan14;
        private bool _supportsDynamicRenderingLocalRead;
        private bool _supportsDynamicRenderingLocalReadStorageResources;
        private bool _supportsDynamicRenderingLocalReadColorAttachments;
        private bool _supportsDynamicRenderingLocalReadDepthStencilAttachments;
        private bool _supportsDynamicRenderingLocalReadMultisampledAttachments;
        private bool _supportsMaintenance4;
        private bool _supportsMaintenance5;
        private bool _supportsExtendedFlags;
        private bool _supportsDescriptorHeap;
        private bool _supportsShaderObject;
        private bool _supportsMemoryBudget;
        private bool _supportsMemoryPriority;
        private bool _supportsAccelerationStructure;
        private bool _supportsRayTracingPipeline;
        private bool _supportsRayQuery;
        private bool _supportsDeviceGeneratedCommands;
        private PhysicalDeviceTransformFeedbackPropertiesEXT _transformFeedbackProperties;
        private PhysicalDeviceVulkan14Properties _vulkan14Properties;
        private PhysicalDeviceShaderObjectPropertiesEXT _shaderObjectProperties;
        private readonly Dictionary<ulong, uint> _renderPassColorAttachmentCounts = new();
        private readonly Dictionary<ulong, Format[]> _renderPassColorAttachmentFormats = new();
        private readonly Dictionary<ulong, string> _renderPassSemanticSignatures = new();
        private readonly Dictionary<Format, bool> _formatColorBlendSupport = new();
        private MemoryDecompressionMethodFlagsNV _nvMemoryDecompressionMethods;
        private ulong _nvMaxMemoryDecompressionIndirectCount;
        private ulong _nvCopyMemoryIndirectSupportedQueues;

        public bool SupportsNvMemoryDecompression => _supportsNvMemoryDecompression && _nvMemoryDecompression is not null;
        public bool SupportsNvCopyMemoryIndirect => _supportsNvCopyMemoryIndirect && _nvCopyMemoryIndirect is not null;
        public bool SupportsExternalMemoryWin32 => _supportsExternalMemoryWin32 && _khrExternalMemoryWin32 is not null;
        public bool SupportsExternalSemaphoreWin32 => _supportsExternalSemaphoreWin32 && _khrExternalSemaphoreWin32 is not null;
        public bool SupportsBufferDeviceAddress => _supportsBufferDeviceAddress;
        public bool SupportsVulkanMeshTaskIndirectCount => _supportsVulkanMeshTaskIndirectCount && _extMeshShader is not null;
        public bool SupportsDynamicRendering => _supportsDynamicRendering;
        public bool SupportsIndexTypeUint8 => _supportsIndexTypeUint8;
        public bool SupportsSynchronization2 => _supportsSynchronization2;
        public bool SupportsDepthClipControl => _supportsDepthClipControl;
        public bool SupportsGraphicsPipelineLibrary => _supportsGraphicsPipelineLibrary;
        public bool SupportsTransformFeedback => _supportsTransformFeedback && _extTransformFeedback is not null;
        public bool SupportsTransformFeedbackGeometryStreams => SupportsTransformFeedback && _supportsTransformFeedbackGeometryStreams;
        public bool SupportsTransformFeedbackQueries => SupportsTransformFeedback && _supportsTransformFeedbackQueries;
        public bool SupportsTransformFeedbackDraw => SupportsTransformFeedback && _supportsTransformFeedbackDraw;
        /// <summary>Core-1.2 hostQueryReset feature: query pools may be reset from the CPU via vkResetQueryPool.</summary>
        public bool SupportsHostQueryReset => _supportsHostQueryReset;
        public bool SupportsVulkanFragmentShadingRate => _supportsVulkanFragmentShadingRate;
        public bool SupportsVulkanFragmentShadingRateAttachment => _supportsVulkanFragmentShadingRateAttachment;
        public bool SupportsVulkanFragmentDensityMap => _supportsVulkanFragmentDensityMap;
        public bool SupportsVulkanFragmentDensityMapDynamic => _supportsVulkanFragmentDensityMapDynamic;
        public PhysicalDeviceTransformFeedbackPropertiesEXT TransformFeedbackProperties => _transformFeedbackProperties;
        public bool SupportsFragmentStoresAndAtomics => _supportsFragmentStoresAndAtomics;
        public bool SupportsVertexPipelineStoresAndAtomics => _supportsVertexPipelineStoresAndAtomics;
        public bool SupportsGeometryShader => _supportsGeometryShader;
        public bool SupportsVulkan14 => _supportsVulkan14;
        public bool SupportsDynamicRenderingLocalRead => _supportsDynamicRenderingLocalRead;
        public bool SupportsDynamicRenderingLocalReadStorageResources => _supportsDynamicRenderingLocalReadStorageResources;
        public bool SupportsDynamicRenderingLocalReadColorAttachments => _supportsDynamicRenderingLocalReadColorAttachments;
        public bool SupportsDynamicRenderingLocalReadDepthStencilAttachments => _supportsDynamicRenderingLocalReadDepthStencilAttachments;
        public bool SupportsDynamicRenderingLocalReadMultisampledAttachments => _supportsDynamicRenderingLocalReadMultisampledAttachments;
        public bool SupportsMaintenance4 => _supportsMaintenance4;
        public bool SupportsMaintenance5 => _supportsMaintenance5;
        public bool SupportsExtendedFlags => _supportsExtendedFlags;
        public bool SupportsDescriptorHeap => _supportsDescriptorHeap;
        public bool SupportsShaderObject => _supportsShaderObject;
        public bool SupportsMemoryBudget => _supportsMemoryBudget;
        public bool SupportsMemoryPriority => _supportsMemoryPriority;
        public bool SupportsAccelerationStructure => _supportsAccelerationStructure;
        public bool SupportsRayTracingPipeline => _supportsRayTracingPipeline;
        public bool SupportsRayQuery => _supportsRayQuery;
        public bool SupportsDeviceGeneratedCommands => _supportsDeviceGeneratedCommands;
        public MemoryDecompressionMethodFlagsNV NvMemoryDecompressionMethods => _nvMemoryDecompressionMethods;
        public ulong NvMaxMemoryDecompressionIndirectCount => _nvMaxMemoryDecompressionIndirectCount;
        public ulong NvCopyMemoryIndirectSupportedQueues => _nvCopyMemoryIndirectSupportedQueues;

        private bool UseCoreDynamicRenderingCommands => _vulkanInstanceApiVersion >= Vk.Version13;
        private bool UseCoreSynchronization2Commands => _vulkanInstanceApiVersion >= Vk.Version13;

        private void CmdBeginDynamicRendering(CommandBuffer commandBuffer, RenderingInfo* renderingInfo)
        {
            if (UseCoreDynamicRenderingCommands)
            {
                Api!.CmdBeginRendering(commandBuffer, renderingInfo);
                return;
            }

            if (_khrDynamicRendering is null)
                throw new InvalidOperationException("VK_KHR_dynamic_rendering command extension is not loaded.");

            _khrDynamicRendering.CmdBeginRendering(commandBuffer, renderingInfo);
        }

        private void CmdEndDynamicRendering(CommandBuffer commandBuffer)
        {
            if (UseCoreDynamicRenderingCommands)
            {
                Api!.CmdEndRendering(commandBuffer);
                return;
            }

            if (_khrDynamicRendering is null)
                throw new InvalidOperationException("VK_KHR_dynamic_rendering command extension is not loaded.");

            _khrDynamicRendering.CmdEndRendering(commandBuffer);
        }

        private Result QueueSubmit2Compat(Queue queue, uint submitCount, SubmitInfo2* submits, Fence fence)
        {
            if (UseCoreSynchronization2Commands)
                return Api!.QueueSubmit2(queue, submitCount, submits, fence);

            if (_khrSynchronization2 is null)
                throw new InvalidOperationException("VK_KHR_synchronization2 command extension is not loaded.");

            return _khrSynchronization2.QueueSubmit2(queue, submitCount, submits, fence);
        }

        private void CmdPipelineBarrier2Compat(CommandBuffer commandBuffer, DependencyInfo* dependencyInfo)
        {
            if (UseCoreSynchronization2Commands)
            {
                Api!.CmdPipelineBarrier2(commandBuffer, dependencyInfo);
                return;
            }

            if (_khrSynchronization2 is null)
                throw new InvalidOperationException("VK_KHR_synchronization2 command extension is not loaded.");

            _khrSynchronization2.CmdPipelineBarrier2(commandBuffer, dependencyInfo);
        }

        internal void RegisterRenderPassColorAttachmentCount(RenderPass renderPass, uint colorAttachmentCount, string? semanticSignature = null)
        {
            if (renderPass.Handle == 0)
                return;

            _renderPassColorAttachmentCounts[renderPass.Handle] = colorAttachmentCount;
            _renderPassColorAttachmentFormats.Remove(renderPass.Handle);
            if (!string.IsNullOrWhiteSpace(semanticSignature))
                _renderPassSemanticSignatures[renderPass.Handle] = semanticSignature!;
        }

        internal void RegisterRenderPassColorAttachmentFormats(RenderPass renderPass, ReadOnlySpan<Format> colorAttachmentFormats, string? semanticSignature = null)
        {
            if (renderPass.Handle == 0)
                return;

            _renderPassColorAttachmentCounts[renderPass.Handle] = (uint)colorAttachmentFormats.Length;
            _renderPassColorAttachmentFormats[renderPass.Handle] = colorAttachmentFormats.ToArray();
            if (!string.IsNullOrWhiteSpace(semanticSignature))
                _renderPassSemanticSignatures[renderPass.Handle] = semanticSignature!;
        }

        internal void UnregisterRenderPass(RenderPass renderPass)
        {
            if (renderPass.Handle == 0)
                return;

            _renderPassColorAttachmentCounts.Remove(renderPass.Handle);
            _renderPassColorAttachmentFormats.Remove(renderPass.Handle);
            _renderPassSemanticSignatures.Remove(renderPass.Handle);
        }

        internal uint GetRenderPassColorAttachmentCount(RenderPass renderPass)
        {
            if (renderPass.Handle != 0 &&
                _renderPassColorAttachmentCounts.TryGetValue(renderPass.Handle, out uint count))
                return count;

            Debug.VulkanWarningEvery(
                $"Vulkan.RenderPassColorCount.Fallback.{renderPass.Handle}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] GetRenderPassColorAttachmentCount fallback to 1 for render pass 0x{0:X} (handle={1}). Registered passes: {2}",
                renderPass.Handle,
                renderPass.Handle,
                _renderPassColorAttachmentCounts.Count);

            return 1u;
        }

        internal Format GetRenderPassColorAttachmentFormat(RenderPass renderPass, uint attachmentIndex)
        {
            if (renderPass.Handle != 0 &&
                _renderPassColorAttachmentFormats.TryGetValue(renderPass.Handle, out Format[]? formats) &&
                attachmentIndex < formats.Length)
            {
                return formats[attachmentIndex];
            }

            return Format.Undefined;
        }

        internal bool SupportsColorAttachmentBlend(Format format)
        {
            if (format == Format.Undefined || VkFormatConversions.IsDepthStencilFormat(format))
                return false;

            if (_formatColorBlendSupport.TryGetValue(format, out bool supported))
                return supported;

            Api!.GetPhysicalDeviceFormatProperties(PhysicalDevice, format, out FormatProperties properties);
            supported = (properties.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBlendBit) != 0;
            _formatColorBlendSupport[format] = supported;
            return supported;
        }

        internal string GetRenderPassSemanticSignature(RenderPass renderPass)
        {
            if (renderPass.Handle != 0 &&
                _renderPassSemanticSignatures.TryGetValue(renderPass.Handle, out string? signature) &&
                !string.IsNullOrWhiteSpace(signature))
            {
                return signature;
            }

            return $"RenderPass:Unregistered:ColorCount={GetRenderPassColorAttachmentCount(renderPass)}";
        }

        private readonly string[] deviceExtensions =
        [
            KhrSwapchain.ExtensionName
        ];

        /// <summary>
        /// Optional extensions that will be enabled if supported by the device.
        /// </summary>
        private readonly string[] optionalDeviceExtensions =
        [
            "VK_KHR_multiview",
            "VK_KHR_external_memory",
            "VK_KHR_external_semaphore",
            "VK_KHR_external_memory_win32",
            "VK_KHR_external_semaphore_win32",
            "VK_KHR_draw_indirect_count",
            "VK_KHR_synchronization2",
            "VK_KHR_shader_draw_parameters",
            "VK_EXT_shader_viewport_index_layer",
            "VK_EXT_index_type_uint8",
            "VK_EXT_descriptor_indexing",
            VulkanDescriptorHeapExt.ExtensionName,
            VulkanDescriptorHeapExt.ShaderUntypedPointersExtensionName,
            "VK_KHR_buffer_device_address",
            "VK_KHR_dynamic_rendering",
            "VK_KHR_dynamic_rendering_local_read",
            "VK_KHR_maintenance4",
            "VK_KHR_maintenance5",
            "VK_KHR_extended_flags",
            VulkanDepthClipControlExt.ExtensionName,
            "VK_KHR_pipeline_library",
            "VK_EXT_graphics_pipeline_library",
            ExtTransformFeedback.ExtensionName,
            "VK_KHR_fragment_shading_rate",
            "VK_EXT_fragment_density_map",
            "VK_EXT_mesh_shader",
            "VK_EXT_shader_object",
            "VK_EXT_memory_budget",
            "VK_EXT_memory_priority",
            "VK_NV_memory_decompression",
            "VK_NV_copy_memory_indirect"
        ];

        internal static readonly string[] ReportedModernCapabilityExtensionNames =
        [
            "VK_KHR_multiview",
            "VK_KHR_external_memory",
            "VK_KHR_external_semaphore",
            "VK_KHR_external_memory_win32",
            "VK_KHR_external_semaphore_win32",
            "VK_KHR_draw_indirect_count",
            "VK_KHR_synchronization2",
            "VK_KHR_shader_draw_parameters",
            "VK_EXT_shader_viewport_index_layer",
            "VK_EXT_index_type_uint8",
            "VK_KHR_index_type_uint8",
            "VK_EXT_descriptor_indexing",
            "VK_EXT_descriptor_heap",
            "VK_KHR_shader_untyped_pointers",
            "VK_EXT_descriptor_buffer",
            "VK_EXT_shader_object",
            "VK_KHR_buffer_device_address",
            "VK_KHR_dynamic_rendering",
            "VK_KHR_dynamic_rendering_local_read",
            "VK_KHR_maintenance4",
            "VK_KHR_maintenance5",
            "VK_KHR_extended_flags",
            "VK_EXT_depth_clip_control",
            "VK_KHR_pipeline_library",
            "VK_EXT_graphics_pipeline_library",
            "VK_EXT_transform_feedback",
            "VK_KHR_fragment_shading_rate",
            "VK_EXT_fragment_density_map",
            "VK_EXT_mesh_shader",
            "VK_EXT_memory_budget",
            "VK_EXT_memory_priority",
            "VK_KHR_acceleration_structure",
            "VK_KHR_ray_tracing_pipeline",
            "VK_KHR_ray_query",
            "VK_KHR_deferred_host_operations",
            "VK_EXT_device_generated_commands",
            "VK_NV_memory_decompression",
            "VK_NV_copy_memory_indirect",
            KhrDeviceFaultExtensionName,
            ExtDeviceFaultExtensionName,
            ExtDeviceAddressBindingReportExtensionName,
            NvDeviceDiagnosticCheckpointsExtensionName,
            NvDeviceDiagnosticsConfigExtensionName
        ];

        private string[] GetRequiredExtensions()
        {
            var glfwExtensions = Window!.VkSurface!.GetRequiredExtensions(out var glfwExtensionCount);
            var extensions = SilkMarshal.PtrToStringArray((nint)glfwExtensions, (int)glfwExtensionCount);
            var openXrRequirements = OpenXRAPI.GetRequestedVulkanRuntimeRequirements();
            if (openXrRequirements.InstanceExtensions.Length > 0)
                extensions = [.. extensions, .. openXrRequirements.InstanceExtensions];

            if (EnableValidationLayers || _diagnosticOptions.EnableDebugUtils)
                extensions = [.. extensions, ExtDebugUtils.ExtensionName];

            return [.. extensions
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)];
        }
    }
}

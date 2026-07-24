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
        /// Indicates the name of the VK_EXT_swapchain_colorspace extension.
        /// </summary>
        private const string ExtSwapchainColorspaceExtensionName = "VK_EXT_swapchain_colorspace";

        private bool _supportsSwapchainColorspace;
        /// <summary>
        /// The KHR_draw_indirect_count extension handle, loaded at device creation if available.
        /// </summary>
        private KhrDrawIndirectCount? _khrDrawIndirectCount;
        /// <summary>
        /// Indicates whether the VK_KHR_dynamic_rendering extension is supported and loaded.
        /// </summary>
        private KhrDynamicRendering? _khrDynamicRendering;
        /// <summary>
        /// Indicates whether the VK_KHR_synchronization2 extension is supported and loaded.
        /// </summary>
        private KhrSynchronization2? _khrSynchronization2;
        /// <summary>
        /// Indicates whether the VK_EXT_mesh_shader extension is supported and loaded.
        /// </summary>
        private ExtMeshShader? _extMeshShader;
        /// <summary>
        /// Indicates whether the VK_EXT_transform_feedback extension is supported and loaded.
        /// </summary>
        private ExtTransformFeedback? _extTransformFeedback;
        /// <summary>
        /// Indicates whether the VK_KHR_external_memory_win32 extension is supported and loaded.
        /// </summary>
        private KhrExternalMemoryWin32? _khrExternalMemoryWin32;
        /// <summary>
        /// Indicates whether the VK_KHR_external_semaphore_win32 extension is supported and loaded.
        /// </summary>
        private KhrExternalSemaphoreWin32? _khrExternalSemaphoreWin32;

        /// <summary>
        /// Indicates whether VK_KHR_draw_indirect_count extension is supported and loaded.
        /// </summary>
        private bool _supportsDrawIndirectCount;
        /// <summary>
        /// Indicates whether indirect-count commands are dispatched through the Vulkan 1.2 core entry point.
        /// </summary>
        private bool _usesCoreDrawIndirectCountCommands;
        /// <summary>
        /// Indicates whether the logical device enabled the core multi-draw-indirect feature.
        /// </summary>
        private bool _supportsMultiDrawIndirect;
        /// <summary>
        /// Indicates whether the logical device enabled nonzero firstInstance values for indirect draws.
        /// </summary>
        private bool _supportsDrawIndirectFirstInstance;
        /// <summary>
        /// Indicates whether the Vulkan task shader feature is supported.
        /// </summary>
        private bool _supportsVulkanTaskShaderFeature;
        /// <summary>
        /// Indicates whether the Vulkan mesh shader feature is supported.
        /// </summary>
        private bool _supportsVulkanMeshShaderFeature;
        /// <summary>
        /// Indicates whether the Vulkan mesh task indirect count feature is supported.
        /// </summary>
        private bool _supportsVulkanMeshTaskIndirectCount;
        /// <summary>
        /// Indicates whether the descriptor indexing feature is supported.
        /// </summary>
        private bool _supportsDescriptorIndexing;
        /// <summary>
        /// Indicates whether the runtime descriptor array feature is supported.
        /// </summary>
        private bool _supportsRuntimeDescriptorArray;
        /// <summary>
        /// Indicates whether the descriptor binding partially bound feature is supported.
        /// </summary>
        private bool _supportsDescriptorBindingPartiallyBound;
        /// <summary>
        /// Indicates whether the descriptor binding update-after-bind feature is supported.
        /// </summary>
        private bool _supportsDescriptorBindingUpdateAfterBind;
        /// <summary>
        /// Indicates whether the descriptor binding storage image update-after-bind feature is supported.
        /// </summary>
        private bool _supportsDescriptorBindingStorageImageUpdateAfterBind;
        /// <summary>
        /// Indicates whether the external memory Win32 feature is supported.
        /// </summary>
        private bool _supportsExternalMemoryWin32;
        /// <summary>
        /// Indicates whether the external semaphore Win32 feature is supported.
        /// </summary>
        private bool _supportsExternalSemaphoreWin32;
        /// <summary>
        /// Indicates whether the NV memory decompression feature is supported.
        /// </summary>
        private NVMemoryDecompression? _nvMemoryDecompression;
        /// <summary>
        /// Indicates whether the NV copy memory indirect feature is supported.
        /// </summary>
        private NVCopyMemoryIndirect? _nvCopyMemoryIndirect;
        /// <summary>
        /// Indicates whether the buffer device address feature is supported.
        /// </summary>
        private bool _supportsBufferDeviceAddress;
        /// <summary>
        /// Indicates whether the NV memory decompression feature is supported.
        /// </summary>
        private bool _supportsNvMemoryDecompression;
        /// <summary>
        /// Indicates whether the NV copy memory indirect feature is supported.
        /// </summary>
        private bool _supportsNvCopyMemoryIndirect;
        /// <summary>
        /// Indicates whether the dynamic rendering feature is supported.
        /// </summary>
        private bool _supportsDynamicRendering;
        /// <summary>
        /// Indicates whether the index type uint8 feature is supported.
        /// </summary>
        private bool _supportsIndexTypeUint8;
        /// <summary>
        /// Indicates whether the synchronization2 feature is supported.
        /// </summary>
        private bool _supportsSynchronization2;
        /// <summary>
        /// Indicates whether the depth clip control feature is supported.
        /// </summary>
        private bool _supportsDepthClipControl;
        /// <summary>
        /// Indicates whether the graphics pipeline library feature is supported.
        /// </summary>
        private bool _supportsGraphicsPipelineLibrary;
        /// <summary>
        /// Indicates whether the transform feedback feature is supported.
        /// </summary>
        private bool _supportsTransformFeedback;
        /// <summary>
        /// Indicates whether the transform feedback geometry streams feature is supported.
        /// </summary>
        private bool _supportsTransformFeedbackGeometryStreams;
        /// <summary>
        /// Indicates whether the transform feedback queries feature is supported.
        /// </summary>
        private bool _supportsTransformFeedbackQueries;
        /// <summary>
        /// Indicates whether the transform feedback draw feature is supported.
        /// </summary>
        private bool _supportsTransformFeedbackDraw;
        /// <summary>
        /// Indicates whether the host query reset feature is supported.
        /// </summary>
        private bool _supportsHostQueryReset;
        /// <summary>
        /// Indicates whether the Vulkan fragment shading rate feature is supported.
        /// </summary>
        private bool _supportsVulkanFragmentShadingRate;
        /// <summary>
        /// Indicates whether the Vulkan fragment shading rate attachment feature is supported.
        /// </summary>
        private bool _supportsVulkanFragmentShadingRateAttachment;
        /// <summary>
        /// Indicates whether the Vulkan fragment density map feature is supported.
        /// </summary>
        private bool _supportsVulkanFragmentDensityMap;
        /// <summary>
        /// Indicates whether the Vulkan fragment density map dynamic feature is supported.
        /// </summary>
        private bool _supportsVulkanFragmentDensityMapDynamic;
        /// <summary>
        /// Indicates whether the fragment stores and atomics feature is supported.
        /// </summary>
        private bool _supportsFragmentStoresAndAtomics;
        /// <summary>
        /// Indicates whether the vertex pipeline stores and atomics feature is supported.
        /// </summary>
        private bool _supportsVertexPipelineStoresAndAtomics;
        /// <summary>
        /// Indicates whether the geometry shader feature is supported.
        /// </summary>
        private bool _supportsGeometryShader;
        /// <summary>
        /// Indicates whether Vulkan 1.4 is supported.
        /// </summary>
        private bool _supportsVulkan14;
        /// <summary>
        /// Indicates whether the dynamic rendering local read feature is supported.
        /// </summary>
        private bool _supportsDynamicRenderingLocalRead;
        /// <summary>
        /// Indicates whether the dynamic rendering local read storage resources feature is supported.
        /// </summary>
        private bool _supportsDynamicRenderingLocalReadStorageResources;
        /// <summary>
        /// Indicates whether the dynamic rendering local read color attachments feature is supported.
        /// </summary>
        private bool _supportsDynamicRenderingLocalReadColorAttachments;
        /// <summary>
        /// Indicates whether the dynamic rendering local read depth stencil attachments feature is supported.
        /// </summary>
        private bool _supportsDynamicRenderingLocalReadDepthStencilAttachments;
        /// <summary>
        /// Indicates whether the dynamic rendering local read multisampled attachments feature is supported.
        /// </summary>
        private bool _supportsDynamicRenderingLocalReadMultisampledAttachments;
        /// <summary>
        /// Indicates whether the maintenance4 feature is supported.
        /// </summary>
        private bool _supportsMaintenance4;
        /// <summary>
        /// Indicates whether the maintenance5 feature is supported.
        /// </summary>
        private bool _supportsMaintenance5;
        /// <summary>
        /// Indicates whether the extended flags feature is supported.
        /// </summary>
        private bool _supportsExtendedFlags;
        /// <summary>
        /// Indicates whether the descriptor heap feature is supported.
        /// </summary>
        private bool _supportsDescriptorHeap;
        /// <summary>
        /// Indicates whether the shader object feature is supported.
        /// </summary>
        private bool _supportsShaderObject;
        /// <summary>
        /// Indicates whether the memory budget feature is supported.
        /// </summary>
        private bool _supportsMemoryBudget;
        /// <summary>
        /// Indicates whether the memory priority feature is supported.
        /// </summary>
        private bool _supportsMemoryPriority;
        /// <summary>
        /// Indicates whether the acceleration structure feature is supported.
        /// </summary>
        private bool _supportsAccelerationStructure;
        /// <summary>
        /// Indicates whether the ray tracing pipeline feature is supported.
        /// </summary>
        private bool _supportsRayTracingPipeline;
        /// <summary>
        /// Indicates whether the ray query feature is supported.
        /// </summary>
        private bool _supportsRayQuery;
        /// <summary>
        /// Indicates whether the device generated commands feature is supported.
        /// </summary>
        private bool _supportsDeviceGeneratedCommands;

        /// <summary>
        /// Indicates the properties of the transform feedback feature.
        /// </summary>
        private PhysicalDeviceTransformFeedbackPropertiesEXT _transformFeedbackProperties;
        /// <summary>
        /// Indicates the properties of the Vulkan 1.4 feature set.
        /// </summary>
        private PhysicalDeviceVulkan14Properties _vulkan14Properties;
        /// <summary>
        /// Indicates the properties of the shader object feature.
        /// </summary>
        private PhysicalDeviceShaderObjectPropertiesEXT _shaderObjectProperties;
        /// <summary>
        /// Indicates the properties of the fragment shading rate feature.
        /// </summary>
        private PhysicalDeviceFragmentShadingRatePropertiesKHR _fragmentShadingRateProperties;
        /// <summary>
        /// Indicates the color attachment counts for render passes.
        /// </summary>
        private readonly Dictionary<ulong, uint> _renderPassColorAttachmentCounts = new();
        /// <summary>
        /// Indicates the color attachment formats for render passes.
        /// </summary>
        private readonly Dictionary<ulong, Format[]> _renderPassColorAttachmentFormats = new();
        /// <summary>
        /// Indicates the semantic signatures for render passes.
        /// </summary>
        private readonly Dictionary<ulong, string> _renderPassSemanticSignatures = new();
        /// <summary>
        /// Indicates whether a format supports color blending.
        /// </summary>
        private readonly Dictionary<Format, bool> _formatColorBlendSupport = new();
        /// <summary>
        /// Indicates the NV memory decompression methods supported by the device.
        /// </summary>
        private MemoryDecompressionMethodFlagsNV _nvMemoryDecompressionMethods;
        /// <summary>
        /// Indicates the maximum memory decompression indirect count supported by the device.
        /// </summary>
        private ulong _nvMaxMemoryDecompressionIndirectCount;
        /// <summary>
        /// Indicates the supported queues for NV copy memory indirect operations.
        /// </summary>
        private ulong _nvCopyMemoryIndirectSupportedQueues;

        /// <summary>
        /// Indicates whether the device supports NV memory decompression through the VK_NV_memory_decompression extension.
        /// </summary>
        public bool SupportsNvMemoryDecompression => _supportsNvMemoryDecompression && _nvMemoryDecompression is not null;
        /// <summary>
        /// Indicates whether the device supports NV copy memory indirect operations through the VK_NV_copy_memory_indirect extension.
        /// </summary>
        public bool SupportsNvCopyMemoryIndirect => _supportsNvCopyMemoryIndirect && _nvCopyMemoryIndirect is not null;
        /// <summary>
        /// Indicates whether the device supports external memory on Win32 through the VK_KHR_external_memory_win32 extension.
        /// </summary>
        public bool SupportsExternalMemoryWin32 => _supportsExternalMemoryWin32 && _khrExternalMemoryWin32 is not null;
        /// <summary>
        /// Indicates whether the device supports external semaphores on Win32 through the VK_KHR_external_semaphore_win32 extension.
        /// </summary>
        public bool SupportsExternalSemaphoreWin32 => _supportsExternalSemaphoreWin32 && _khrExternalSemaphoreWin32 is not null;
        /// <summary>
        /// Indicates whether the device supports buffer device addresses.
        /// </summary>
        public bool SupportsBufferDeviceAddress => _supportsBufferDeviceAddress;
        /// <summary>
        /// Indicates whether the device supports Vulkan mesh task indirect count.
        /// </summary>
        public bool SupportsVulkanMeshTaskIndirectCount => _supportsVulkanMeshTaskIndirectCount && _extMeshShader is not null;
        /// <summary>
        /// Indicates whether the device supports dynamic rendering.
        /// </summary>
        public bool SupportsDynamicRendering => _supportsDynamicRendering;
        /// <summary>
        /// Indicates whether the device supports the index type uint8.
        /// </summary>
        public bool SupportsIndexTypeUint8 => _supportsIndexTypeUint8;
        /// <summary>
        /// Indicates whether the device supports the synchronization2 feature.
        /// </summary>
        public bool SupportsSynchronization2 => _supportsSynchronization2;
        /// <summary>
        /// Indicates whether the device supports depth clip control.
        /// </summary>
        public bool SupportsDepthClipControl => _supportsDepthClipControl;
        /// <summary>
        /// Indicates whether the device supports graphics pipeline library.
        /// </summary>
        public bool SupportsGraphicsPipelineLibrary => _supportsGraphicsPipelineLibrary;
        /// <summary>
        /// Indicates whether the device supports transform feedback.
        /// </summary>
        public bool SupportsTransformFeedback => _supportsTransformFeedback && _extTransformFeedback is not null;
        /// <summary>
        /// Indicates whether the device supports transform feedback geometry streams.
        /// </summary>
        public bool SupportsTransformFeedbackGeometryStreams => SupportsTransformFeedback && _supportsTransformFeedbackGeometryStreams;
        /// <summary>
        /// Indicates whether the device supports transform feedback queries.
        /// </summary>
        public bool SupportsTransformFeedbackQueries => SupportsTransformFeedback && _supportsTransformFeedbackQueries;
        /// <summary>
        /// Indicates whether the device supports transform feedback draw.
        /// </summary>
        public bool SupportsTransformFeedbackDraw => SupportsTransformFeedback && _supportsTransformFeedbackDraw;
        /// <summary>
        /// Core-1.2 hostQueryReset feature: query pools may be reset from the CPU via vkResetQueryPool.
        /// </summary>
        public bool SupportsHostQueryReset => _supportsHostQueryReset;
        /// <summary>
        /// Indicates whether the device supports Vulkan fragment shading rate.
        /// </summary>
        public bool SupportsVulkanFragmentShadingRate => _supportsVulkanFragmentShadingRate;
        /// <summary>
        /// Indicates whether the device supports Vulkan fragment shading rate attachment.
        /// </summary>
        public bool SupportsVulkanFragmentShadingRateAttachment => _supportsVulkanFragmentShadingRateAttachment;
        /// <summary>
        /// Gets the fragment shading rate properties of the physical device.
        /// </summary>
        public PhysicalDeviceFragmentShadingRatePropertiesKHR FragmentShadingRateProperties => _fragmentShadingRateProperties;
        /// <summary>
        /// Indicates whether the device supports Vulkan fragment density map.
        /// </summary>
        public bool SupportsVulkanFragmentDensityMap => _supportsVulkanFragmentDensityMap;
        /// <summary>
        /// Indicates whether the device supports dynamic Vulkan fragment density map.
        /// </summary>
        public bool SupportsVulkanFragmentDensityMapDynamic => _supportsVulkanFragmentDensityMapDynamic;
        /// <summary>
        /// Gets the transform feedback properties of the physical device.
        /// </summary>
        public PhysicalDeviceTransformFeedbackPropertiesEXT TransformFeedbackProperties => _transformFeedbackProperties;
        /// <summary>
        /// Indicates whether the device supports fragment stores and atomics.
        /// </summary>
        public bool SupportsFragmentStoresAndAtomics => _supportsFragmentStoresAndAtomics;
        /// <summary>
        /// Indicates whether the device supports vertex pipeline stores and atomics.
        /// </summary>
        public bool SupportsVertexPipelineStoresAndAtomics => _supportsVertexPipelineStoresAndAtomics;
        /// <summary>
        /// Indicates whether the device supports geometry shader.
        /// </summary>
        public bool SupportsGeometryShader => _supportsGeometryShader;
        /// <summary>
        /// Indicates whether the device supports Vulkan 1.4.
        /// </summary>
        public bool SupportsVulkan14 => _supportsVulkan14;
        /// <summary>
        /// Indicates whether the device supports dynamic rendering local read.
        /// </summary>
        public bool SupportsDynamicRenderingLocalRead => _supportsDynamicRenderingLocalRead;
        /// <summary>
        /// Indicates whether the device supports dynamic rendering local read storage resources.
        /// </summary>
        public bool SupportsDynamicRenderingLocalReadStorageResources => _supportsDynamicRenderingLocalReadStorageResources;
        /// <summary>
        /// Indicates whether the device supports dynamic rendering local read color attachments.
        /// </summary>
        public bool SupportsDynamicRenderingLocalReadColorAttachments => _supportsDynamicRenderingLocalReadColorAttachments;
        /// <summary>
        /// Indicates whether the device supports dynamic rendering local read depth stencil attachments.
        /// </summary>
        public bool SupportsDynamicRenderingLocalReadDepthStencilAttachments => _supportsDynamicRenderingLocalReadDepthStencilAttachments;
        /// <summary>
        /// Indicates whether the device supports dynamic rendering local read multisampled attachments.
        /// </summary>
        public bool SupportsDynamicRenderingLocalReadMultisampledAttachments => _supportsDynamicRenderingLocalReadMultisampledAttachments;
        /// <summary>
        /// Indicates whether the device supports maintenance 4.
        /// </summary>
        public bool SupportsMaintenance4 => _supportsMaintenance4;
        /// <summary>
        /// Indicates whether the device supports maintenance 5.
        /// </summary>
        public bool SupportsMaintenance5 => _supportsMaintenance5;
        /// <summary>
        /// Indicates whether the device supports extended flags.
        /// </summary>
        public bool SupportsExtendedFlags => _supportsExtendedFlags;
        /// <summary>
        /// Indicates whether the device supports descriptor heap.
        /// </summary>
        public bool SupportsDescriptorHeap => _supportsDescriptorHeap;
        /// <summary>
        /// Indicates whether the device supports shader object.
        /// </summary>
        public bool SupportsShaderObject => _supportsShaderObject;
        /// <summary>
        /// Indicates whether the device supports memory budget.
        /// </summary>
        public bool SupportsMemoryBudget => _supportsMemoryBudget;
        /// <summary>
        /// Indicates whether the device supports memory priority.
        /// </summary>
        public bool SupportsMemoryPriority => _supportsMemoryPriority;
        /// <summary>
        /// Indicates whether the device supports acceleration structure.
        /// </summary>
        public bool SupportsAccelerationStructure => _supportsAccelerationStructure;
        /// <summary>
        /// Indicates whether the device supports ray tracing pipeline.
        /// </summary>
        public bool SupportsRayTracingPipeline => _supportsRayTracingPipeline;
        /// <summary>
        /// Indicates whether the device supports ray query.
        /// </summary>
        public bool SupportsRayQuery => _supportsRayQuery;
        /// <summary>
        /// Indicates whether the device supports device generated commands.
        /// </summary>
        public bool SupportsDeviceGeneratedCommands => _supportsDeviceGeneratedCommands;
        /// <summary>
        /// Indicates the memory decompression methods supported by the device.
        /// </summary>
        public MemoryDecompressionMethodFlagsNV NvMemoryDecompressionMethods => _nvMemoryDecompressionMethods;
        /// <summary>
        /// Indicates the maximum memory decompression indirect count supported by the device.
        /// </summary>
        public ulong NvMaxMemoryDecompressionIndirectCount => _nvMaxMemoryDecompressionIndirectCount;
        /// <summary>
        /// Indicates the supported queues for copy memory indirect operations.
        /// </summary>
        public ulong NvCopyMemoryIndirectSupportedQueues => _nvCopyMemoryIndirectSupportedQueues;

        /// <summary>
        /// Indicates whether the device should use core dynamic rendering commands based on the Vulkan instance API version.
        /// </summary>
        private bool UseCoreDynamicRenderingCommands => _vulkanInstanceApiVersion >= Vk.Version13;
        /// <summary>
        /// Indicates whether the device should use core synchronization 2 commands based on the Vulkan instance API version.
        /// </summary>
        private bool UseCoreSynchronization2Commands => _vulkanInstanceApiVersion >= Vk.Version13;

        /// <summary>
        /// Begins dynamic rendering for the specified command buffer with the provided rendering information.
        /// </summary>
        /// <param name="commandBuffer">The command buffer for which dynamic rendering will begin.</param>
        /// <param name="renderingInfo">The rendering information describing the attachments and rendering parameters.</param>
        /// <exception cref="InvalidOperationException">Thrown if the appropriate dynamic rendering command extension is not loaded.</exception>
        private void CmdBeginDynamicRendering(CommandBuffer commandBuffer, RenderingInfo* renderingInfo)
        {
            // Ensure that the rendering information is not null before proceeding.
            if (renderingInfo is not null)
            {
                // Track resources for each color attachment in the rendering information.
                for (int i = 0; i < renderingInfo->ColorAttachmentCount; i++)
                {
                    RenderingAttachmentInfo attachment = renderingInfo->PColorAttachments[i];
                    TrackVulkanCommandBufferResource(
                        commandBuffer,
                        ObjectType.ImageView,
                        attachment.ImageView.Handle,
                        "DynamicRendering.ColorAttachment");
                    TrackVulkanCommandBufferResource(
                        commandBuffer,
                        ObjectType.ImageView,
                        attachment.ResolveImageView.Handle,
                        "DynamicRendering.ColorResolveAttachment");
                    EnsureVulkanImageViewAvailableForCommandRecording(
                        commandBuffer,
                        attachment.ImageView,
                        "DynamicRendering.ColorAttachment");
                    EnsureVulkanImageViewAvailableForCommandRecording(
                        commandBuffer,
                        attachment.ResolveImageView,
                        "DynamicRendering.ColorResolveAttachment");
                }

                // Track resources for the depth attachment if it exists.
                if (renderingInfo->PDepthAttachment is not null)
                {
                    TrackVulkanCommandBufferResource(
                        commandBuffer,
                        ObjectType.ImageView,
                        renderingInfo->PDepthAttachment->ImageView.Handle,
                        "DynamicRendering.DepthAttachment");
                    TrackVulkanCommandBufferResource(
                        commandBuffer,
                        ObjectType.ImageView,
                        renderingInfo->PDepthAttachment->ResolveImageView.Handle,
                        "DynamicRendering.DepthResolveAttachment");
                    EnsureVulkanImageViewAvailableForCommandRecording(
                        commandBuffer,
                        renderingInfo->PDepthAttachment->ImageView,
                        "DynamicRendering.DepthAttachment");
                    EnsureVulkanImageViewAvailableForCommandRecording(
                        commandBuffer,
                        renderingInfo->PDepthAttachment->ResolveImageView,
                        "DynamicRendering.DepthResolveAttachment");
                }

                // Track resources for the stencil attachment if it exists.
                if (renderingInfo->PStencilAttachment is not null)
                {
                    TrackVulkanCommandBufferResource(
                        commandBuffer,
                        ObjectType.ImageView,
                        renderingInfo->PStencilAttachment->ImageView.Handle,
                        "DynamicRendering.StencilAttachment");
                    TrackVulkanCommandBufferResource(
                        commandBuffer,
                        ObjectType.ImageView,
                        renderingInfo->PStencilAttachment->ResolveImageView.Handle,
                        "DynamicRendering.StencilResolveAttachment");
                    EnsureVulkanImageViewAvailableForCommandRecording(
                        commandBuffer,
                        renderingInfo->PStencilAttachment->ImageView,
                        "DynamicRendering.StencilAttachment");
                    EnsureVulkanImageViewAvailableForCommandRecording(
                        commandBuffer,
                        renderingInfo->PStencilAttachment->ResolveImageView,
                        "DynamicRendering.StencilResolveAttachment");
                }
            }

            // Streamline 2.12 frame generation is provisioned through the Vulkan extension
            // contract. Keep recording on the KHR command aliases while it is active instead
            // of crossing back through Vulkan 1.4's core dispatch table after DLSS-G hooks
            // have engaged. Both entry points have identical Vulkan semantics.
            bool useKhrDynamicRendering = IsStreamlineFrameGenerationRequested && _khrDynamicRendering is not null;
            if (UseCoreDynamicRenderingCommands && !useKhrDynamicRendering)
            {
                Api!.CmdBeginRendering(commandBuffer, renderingInfo);
                return;
            }

            if (_khrDynamicRendering is null)
                throw new InvalidOperationException("VK_KHR_dynamic_rendering command extension is not loaded.");

            _khrDynamicRendering.CmdBeginRendering(commandBuffer, renderingInfo);
        }

        /// <summary>
        /// Ends dynamic rendering for the specified command buffer.
        /// </summary>
        /// <param name="commandBuffer">The command buffer for which dynamic rendering will be ended.</param>
        /// <exception cref="InvalidOperationException">Thrown if the appropriate dynamic rendering command extension is not loaded.</exception>
        private void CmdEndDynamicRendering(CommandBuffer commandBuffer)
        {
            bool useKhrDynamicRendering = IsStreamlineFrameGenerationRequested && _khrDynamicRendering is not null;
            if (UseCoreDynamicRenderingCommands && !useKhrDynamicRendering)
            {
                Api!.CmdEndRendering(commandBuffer);
                return;
            }

            if (_khrDynamicRendering is null)
                throw new InvalidOperationException("VK_KHR_dynamic_rendering command extension is not loaded.");

            _khrDynamicRendering.CmdEndRendering(commandBuffer);
        }

        /// <summary>
        /// Submits one or more command buffers to a queue using the appropriate synchronization commands, 
        /// either core or via the VK_KHR_synchronization2 extension.
        /// </summary>
        /// <param name="queue">The queue to which the command buffers will be submitted.</param>
        /// <param name="submitCount">The number of command buffer submissions.</param>
        /// <param name="submits">A pointer to an array of SubmitInfo2 structures describing the command buffer submissions.</param>
        /// <param name="fence">An optional fence to signal once the submissions have completed.</param>
        /// <returns>A Result indicating the success or failure of the queue submission.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the appropriate synchronization command extension is not loaded.</exception>
        private Result QueueSubmit2Compat(Queue queue, uint submitCount, SubmitInfo2* submits, Fence fence)
        {
            // Use the core synchronization2 commands if available, 
            // otherwise fall back to the VK_KHR_synchronization2 extension.
            if (UseCoreSynchronization2Commands)
                return Api!.QueueSubmit2(queue, submitCount, submits, fence);

            if (_khrSynchronization2 is null)
                throw new InvalidOperationException("VK_KHR_synchronization2 command extension is not loaded.");

            return _khrSynchronization2.QueueSubmit2(queue, submitCount, submits, fence);
        }

        /// <summary>
        /// Issues a pipeline barrier using the appropriate synchronization commands, 
        /// either core or via the VK_KHR_synchronization2 extension.
        /// </summary>
        /// <param name="commandBuffer">The command buffer on which to issue the pipeline barrier.</param>
        /// <param name="dependencyInfo">The dependency information describing the pipeline barrier.</param>
        /// <exception cref="InvalidOperationException">Thrown if the appropriate synchronization command extension is not loaded.</exception>
        private void CmdPipelineBarrier2Compat(CommandBuffer commandBuffer, DependencyInfo* dependencyInfo)
        {
            // Use the core synchronization2 commands if available, 
            // otherwise fall back to the VK_KHR_synchronization2 extension.
            if (UseCoreSynchronization2Commands)
            {
                Api!.CmdPipelineBarrier2(commandBuffer, dependencyInfo);
                return;
            }

            if (_khrSynchronization2 is null)
                throw new InvalidOperationException("VK_KHR_synchronization2 command extension is not loaded.");

            _khrSynchronization2.CmdPipelineBarrier2(commandBuffer, dependencyInfo);
        }

        /// <summary>
        /// Writes a synchronization2 timestamp through the core 1.3 command or
        /// its KHR extension alias, matching the path selected at device setup.
        /// </summary>
        private void CmdWriteTimestamp2Compat(
            CommandBuffer commandBuffer,
            PipelineStageFlags2 stage,
            QueryPool queryPool,
            uint query)
        {
            if (UseCoreSynchronization2Commands)
            {
                Api!.CmdWriteTimestamp2(commandBuffer, stage, queryPool, query);
                return;
            }

            if (_khrSynchronization2 is null)
                throw new InvalidOperationException("VK_KHR_synchronization2 command extension is not loaded.");

            _khrSynchronization2.CmdWriteTimestamp2(commandBuffer, stage, queryPool, query);
        }

        /// <summary>
        /// Registers the color attachment count for the specified render pass.
        /// </summary>
        /// <param name="renderPass">The render pass for which to register the color attachment count.</param>
        /// <param name="colorAttachmentCount">The number of color attachments for the render pass.</param>
        /// <param name="semanticSignature">An optional semantic signature associated with the render pass.</param>
        internal void RegisterRenderPassColorAttachmentCount(RenderPass renderPass, uint colorAttachmentCount, string? semanticSignature = null)
        {
            if (renderPass.Handle == 0)
                return;

            RegisterVulkanResource(ObjectType.RenderPass, renderPass.Handle, "RenderPass");
            _renderPassColorAttachmentCounts[renderPass.Handle] = colorAttachmentCount;
            _renderPassColorAttachmentFormats.Remove(renderPass.Handle);
            if (!string.IsNullOrWhiteSpace(semanticSignature))
                _renderPassSemanticSignatures[renderPass.Handle] = semanticSignature!;
        }

        /// <summary>
        /// Registers the color attachment formats for the specified render pass.
        /// </summary>
        /// <param name="renderPass">The render pass for which to register the color attachment formats.</param>
        /// <param name="colorAttachmentFormats">The array of color attachment formats to register.</param>
        /// <param name="semanticSignature">An optional semantic signature associated with the render pass.</param>
        internal void RegisterRenderPassColorAttachmentFormats(RenderPass renderPass, ReadOnlySpan<Format> colorAttachmentFormats, string? semanticSignature = null)
        {
            if (renderPass.Handle == 0)
                return;

            RegisterVulkanResource(ObjectType.RenderPass, renderPass.Handle, "RenderPass");
            _renderPassColorAttachmentCounts[renderPass.Handle] = (uint)colorAttachmentFormats.Length;
            _renderPassColorAttachmentFormats[renderPass.Handle] = colorAttachmentFormats.ToArray();
            if (!string.IsNullOrWhiteSpace(semanticSignature))
                _renderPassSemanticSignatures[renderPass.Handle] = semanticSignature!;
        }

        /// <summary>
        /// Unregisters the specified render pass and removes its associated color attachment count, formats, and semantic signature.
        /// </summary>
        /// <param name="renderPass">The render pass to unregister.</param>
        internal void UnregisterRenderPass(RenderPass renderPass)
        {
            if (renderPass.Handle == 0)
                return;

            _renderPassColorAttachmentCounts.Remove(renderPass.Handle);
            _renderPassColorAttachmentFormats.Remove(renderPass.Handle);
            _renderPassSemanticSignatures.Remove(renderPass.Handle);
        }

        /// <summary>
        /// Gets the number of color attachments for the specified render pass.
        /// </summary>
        /// <param name="renderPass">The render pass for which to retrieve the color attachment count.</param>
        /// <returns>The number of color attachments for the specified render pass, or 1 if not found.</returns>
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

        /// <summary>
        /// Gets the format of a color attachment for the specified render pass and attachment index.
        /// </summary>
        /// <param name="renderPass">The render pass for which to retrieve the color attachment format.</param>
        /// <param name="attachmentIndex">The index of the color attachment within the render pass.</param>
        /// <returns>The format of the specified color attachment, or Format.Undefined if not found.</returns>
        internal Format GetRenderPassColorAttachmentFormat(RenderPass renderPass, uint attachmentIndex)
            => renderPass.Handle != 0 &&
                _renderPassColorAttachmentFormats.TryGetValue(renderPass.Handle, out Format[]? formats) &&
                attachmentIndex < formats.Length
                    ? formats[attachmentIndex]
                    : Format.Undefined;

        /// <summary>
        /// Determines whether the specified format supports color attachment blending.
        /// </summary>
        /// <param name="format">The Vulkan format to check for color attachment blend support.</param>
        /// <returns>True if the format supports color attachment blending; otherwise, false.</returns>
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

        /// <summary>
        /// Gets the semantic signature for the specified render pass.
        /// </summary>
        /// <param name="renderPass">The render pass for which to get the semantic signature.</param>
        /// <returns>A string representing the semantic signature of the render pass.</returns>
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

        /// <summary>
        /// The list of required Vulkan device extensions for the application.
        /// </summary>
        private readonly string[] _requiredDeviceExtensions =
        [
            KhrSwapchain.ExtensionName
        ];

        /// <summary>
        /// Optional extensions that will be enabled if supported by the device.
        /// </summary>
        private readonly string[] _optionalDeviceExtensions =
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
            "VK_EXT_pipeline_creation_cache_control",
            ExtTransformFeedback.ExtensionName,
            "VK_EXT_primitives_generated_query",
            "VK_KHR_fragment_shading_rate",
            "VK_EXT_fragment_density_map",
            "VK_EXT_mesh_shader",
            "VK_EXT_shader_object",
            "VK_EXT_memory_budget",
            "VK_EXT_memory_priority",
            SwapchainMaintenance1ExtensionName,
            "VK_NV_memory_decompression",
            "VK_NV_copy_memory_indirect"
        ];

        /// <summary>
        /// Gets the list of Vulkan extensions that indicate modern capabilities reported by the device.
        /// </summary>
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
            "VK_EXT_pipeline_creation_cache_control",
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

        /// <summary>
        /// Gets the list of required Vulkan instance extensions for the application.
        /// </summary>
        /// <returns>An array of strings representing the required Vulkan instance extensions.</returns>
        private string[] GetRequiredExtensions()
        {
            var glfwExtensions = Window!.VkSurface!.GetRequiredExtensions(out var glfwExtensionCount);
            var extensions = SilkMarshal.PtrToStringArray((nint)glfwExtensions, (int)glfwExtensionCount);
            _supportsSwapchainColorspace = IsInstanceExtensionAvailable(ExtSwapchainColorspaceExtensionName);
            if (_supportsSwapchainColorspace)
                extensions = [.. extensions, ExtSwapchainColorspaceExtensionName];
            _surfacePresentScalingInstanceExtensionsEnabled =
                IsInstanceExtensionAvailable(GetSurfaceCapabilities2ExtensionName) &&
                IsInstanceExtensionAvailable(SurfaceMaintenance1ExtensionName);
            if (_surfacePresentScalingInstanceExtensionsEnabled)
            {
                extensions =
                [
                    .. extensions,
                    GetSurfaceCapabilities2ExtensionName,
                    SurfaceMaintenance1ExtensionName,
                ];
            }
            var openXrRequirements = OpenXRAPI.GetRequestedVulkanRuntimeRequirements();
            if (openXrRequirements.InstanceExtensions.Length > 0)
                extensions = [.. extensions, .. openXrRequirements.InstanceExtensions];
            if (_streamlineRequiredInstanceExtensions.Length > 0)
            {
                foreach (string extension in _streamlineRequiredInstanceExtensions)
                {
                    if (!IsInstanceExtensionAvailable(extension))
                        throw new NotSupportedException($"Streamline requires unavailable Vulkan instance extension '{extension}'.");
                }

                extensions = [.. extensions, .. _streamlineRequiredInstanceExtensions];
            }

            if (EnableValidationLayers || _diagnosticOptions.EnableDebugUtils)
                extensions = [.. extensions, ExtDebugUtils.ExtensionName];

            return [.. extensions
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)];
        }

        /// <summary>
        /// Determines whether a specific Vulkan instance extension is available on the system.
        /// </summary>
        /// <param name="extensionName">The name of the Vulkan instance extension to check for availability.</param>
        /// <returns>True if the extension is available; otherwise, false.</returns>
        private bool IsInstanceExtensionAvailable(string extensionName)
        {
            uint extensionCount = 0;
            if (Api!.EnumerateInstanceExtensionProperties((byte*)null, ref extensionCount, null) != Result.Success ||
                extensionCount == 0)
            {
                return false;
            }

            ExtensionProperties[] properties = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* propertiesPtr = properties)
            {
                if (Api.EnumerateInstanceExtensionProperties((byte*)null, ref extensionCount, propertiesPtr) != Result.Success)
                    return false;
            }

            for (int i = 0; i < extensionCount; i++)
            {
                string? availableName;
                fixed (byte* extensionNamePtr = properties[i].ExtensionName)
                    availableName = SilkMarshal.PtrToString((nint)extensionNamePtr);
                if (string.Equals(availableName, extensionName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}

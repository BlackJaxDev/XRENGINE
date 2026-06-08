using System;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.NV;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        /// <summary>
        /// The KHR_draw_indirect_count extension handle, loaded at device creation if available.
        /// </summary>
        private KhrDrawIndirectCount? _khrDrawIndirectCount;
        private ExtMeshShader? _extMeshShader;
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
        private bool _supportsFragmentStoresAndAtomics;
        private bool _supportsVertexPipelineStoresAndAtomics;
        private bool _supportsGeometryShader;
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
        public bool SupportsFragmentStoresAndAtomics => _supportsFragmentStoresAndAtomics;
        public bool SupportsVertexPipelineStoresAndAtomics => _supportsVertexPipelineStoresAndAtomics;
        public bool SupportsGeometryShader => _supportsGeometryShader;
        public MemoryDecompressionMethodFlagsNV NvMemoryDecompressionMethods => _nvMemoryDecompressionMethods;
        public ulong NvMaxMemoryDecompressionIndirectCount => _nvMaxMemoryDecompressionIndirectCount;
        public ulong NvCopyMemoryIndirectSupportedQueues => _nvCopyMemoryIndirectSupportedQueues;

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
            "VK_EXT_index_type_uint8",
            "VK_EXT_descriptor_indexing",
            "VK_KHR_buffer_device_address",
            "VK_KHR_dynamic_rendering",
            "VK_KHR_maintenance4",
            VulkanDepthClipControlExt.ExtensionName,
            "VK_EXT_mesh_shader",
            "VK_NV_memory_decompression",
            "VK_NV_copy_memory_indirect"
        ];

        private string[] GetRequiredExtensions()
        {
            var glfwExtensions = Window!.VkSurface!.GetRequiredExtensions(out var glfwExtensionCount);
            var extensions = SilkMarshal.PtrToStringArray((nint)glfwExtensions, (int)glfwExtensionCount);

            if (EnableValidationLayers)
                return [.. extensions, ExtDebugUtils.ExtensionName];

            return extensions;
        }
    }
}

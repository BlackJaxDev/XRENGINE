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

        /// <summary>
        /// Indicates whether VK_KHR_draw_indirect_count extension is supported and loaded.
        /// </summary>
        private bool _supportsDrawIndirectCount;
        private bool _supportsDescriptorIndexing;
        private bool _supportsRuntimeDescriptorArray;
        private bool _supportsDescriptorBindingPartiallyBound;
        private bool _supportsDescriptorBindingUpdateAfterBind;
        private bool _supportsDescriptorBindingStorageImageUpdateAfterBind;
        private NVMemoryDecompression? _nvMemoryDecompression;
        private NVCopyMemoryIndirect? _nvCopyMemoryIndirect;
        private bool _supportsBufferDeviceAddress;
        private bool _supportsNvMemoryDecompression;
        private bool _supportsNvCopyMemoryIndirect;
        private bool _supportsDynamicRendering;
        private bool _supportsIndexTypeUint8;
        private bool _supportsFragmentStoresAndAtomics;
        private readonly Dictionary<ulong, uint> _renderPassColorAttachmentCounts = new();
        private MemoryDecompressionMethodFlagsNV _nvMemoryDecompressionMethods;
        private ulong _nvMaxMemoryDecompressionIndirectCount;
        private ulong _nvCopyMemoryIndirectSupportedQueues;

        public bool SupportsNvMemoryDecompression => _supportsNvMemoryDecompression && _nvMemoryDecompression is not null;
        public bool SupportsNvCopyMemoryIndirect => _supportsNvCopyMemoryIndirect && _nvCopyMemoryIndirect is not null;
        public bool SupportsBufferDeviceAddress => _supportsBufferDeviceAddress;
        public bool SupportsDynamicRendering => _supportsDynamicRendering;
        public bool SupportsIndexTypeUint8 => _supportsIndexTypeUint8;
        public bool SupportsFragmentStoresAndAtomics => _supportsFragmentStoresAndAtomics;
        public MemoryDecompressionMethodFlagsNV NvMemoryDecompressionMethods => _nvMemoryDecompressionMethods;
        public ulong NvMaxMemoryDecompressionIndirectCount => _nvMaxMemoryDecompressionIndirectCount;
        public ulong NvCopyMemoryIndirectSupportedQueues => _nvCopyMemoryIndirectSupportedQueues;

        internal void RegisterRenderPassColorAttachmentCount(RenderPass renderPass, uint colorAttachmentCount)
        {
            if (renderPass.Handle == 0)
                return;

            _renderPassColorAttachmentCounts[renderPass.Handle] = colorAttachmentCount;
        }

        internal void UnregisterRenderPass(RenderPass renderPass)
        {
            if (renderPass.Handle == 0)
                return;

            _renderPassColorAttachmentCounts.Remove(renderPass.Handle);
        }

        internal uint GetRenderPassColorAttachmentCount(RenderPass renderPass)
        {
            if (renderPass.Handle != 0 &&
                _renderPassColorAttachmentCounts.TryGetValue(renderPass.Handle, out uint count))
                return count;

            return 1u;
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
            "VK_KHR_draw_indirect_count",
            "VK_KHR_shader_draw_parameters",
            "VK_EXT_index_type_uint8",
            "VK_EXT_descriptor_indexing",
            "VK_KHR_dynamic_rendering",
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
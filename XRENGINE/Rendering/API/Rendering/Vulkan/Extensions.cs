using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.NV;

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
        private NVMemoryDecompression? _nvMemoryDecompression;
        private NVCopyMemoryIndirect? _nvCopyMemoryIndirect;
        private bool _supportsBufferDeviceAddress;
        private bool _supportsNvMemoryDecompression;
        private bool _supportsNvCopyMemoryIndirect;
        private MemoryDecompressionMethodFlagsNV _nvMemoryDecompressionMethods;
        private ulong _nvMaxMemoryDecompressionIndirectCount;
        private ulong _nvCopyMemoryIndirectSupportedQueues;

        public bool SupportsNvMemoryDecompression => _supportsNvMemoryDecompression && _nvMemoryDecompression is not null;
        public bool SupportsNvCopyMemoryIndirect => _supportsNvCopyMemoryIndirect && _nvCopyMemoryIndirect is not null;
        public bool SupportsBufferDeviceAddress => _supportsBufferDeviceAddress;
        public MemoryDecompressionMethodFlagsNV NvMemoryDecompressionMethods => _nvMemoryDecompressionMethods;
        public ulong NvMaxMemoryDecompressionIndirectCount => _nvMaxMemoryDecompressionIndirectCount;
        public ulong NvCopyMemoryIndirectSupportedQueues => _nvCopyMemoryIndirectSupportedQueues;

        private readonly string[] deviceExtensions =
        [
            KhrSwapchain.ExtensionName
        ];

        /// <summary>
        /// Optional extensions that will be enabled if supported by the device.
        /// </summary>
        private readonly string[] optionalDeviceExtensions =
        [
            "VK_KHR_draw_indirect_count",
            "VK_EXT_descriptor_indexing",
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
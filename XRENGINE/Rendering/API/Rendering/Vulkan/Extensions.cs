using Silk.NET.Core.Native;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly string[] deviceExtensions =
        [
            KhrSwapchain.ExtensionName
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
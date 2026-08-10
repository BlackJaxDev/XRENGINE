using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanOutputRuntime
{
    /// <summary>
    /// Captures target-owned presentation and swapchain observations for one
    /// device candidate. The returned facts carry no output runtime, surface,
    /// or native callback into device selection.
    /// </summary>
    internal unsafe VulkanOutputDeviceProbeFacts QueryPhysicalDeviceSelectionFacts(
        PhysicalDevice physicalDevice,
        uint queueFamilyCount)
    {
        if (!TargetPolicy.RequiresPresentQueue)
            return VulkanOutputDeviceProbeFacts.Presentationless;

        KhrSurface surfaceApi = SurfaceApi ?? throw new InvalidOperationException(
            "The Vulkan target did not publish a surface API before physical-device selection.");
        SurfaceKHR surface = Surface;
        if (surface.Handle == 0)
            throw new InvalidOperationException(
                "The Vulkan target did not publish a surface before physical-device selection.");

        bool[] presentationSupport = new bool[queueFamilyCount];
        for (uint queueFamilyIndex = 0; queueFamilyIndex < queueFamilyCount; queueFamilyIndex++)
        {
            Result result = surfaceApi.GetPhysicalDeviceSurfaceSupport(
                physicalDevice,
                queueFamilyIndex,
                surface,
                out Bool32 supportsPresentation);
            if (result != Result.Success)
            {
                throw new InvalidOperationException(
                    $"Vulkan presentation support query failed for queue family {queueFamilyIndex}. Result={result}.");
            }

            presentationSupport[queueFamilyIndex] = supportsPresentation;
        }

        if (!TargetPolicy.RequiresSwapchainOutput)
            return new VulkanOutputDeviceProbeFacts(true, presentationSupport, 0, 0);

        uint formatCount = 0;
        ThrowIfSurfaceQueryFailed(
            surfaceApi.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, null),
            "swapchain format count");
        uint presentModeCount = 0;
        ThrowIfSurfaceQueryFailed(
            surfaceApi.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref presentModeCount, null),
            "swapchain present-mode count");
        return new VulkanOutputDeviceProbeFacts(
            true,
            presentationSupport,
            checked((int)formatCount),
            checked((int)presentModeCount));
    }

    private static void ThrowIfSurfaceQueryFailed(Result result, string query)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Vulkan {query} query failed. Result={result}.");
    }
}

using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using System;
using System.Runtime.InteropServices;
using XREngine.Rendering.Vulkan;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

internal sealed class OpenXrGraphicsSessionException(Result result, string message) : Exception(message)
{
    public Result Result { get; } = result;
}

public unsafe partial class OpenXRAPI
{
    /// <summary>
    /// Creates an OpenXR session using Vulkan graphics binding.
    /// </summary>
    /// <exception cref="Exception">Thrown when session creation fails.</exception>
    internal void CreateVulkanSession()
    {
        if (Window is null)
            throw new Exception("Window is null");

        var requirements = new GraphicsRequirementsVulkanKHR
        {
            Type = StructureType.GraphicsRequirementsVulkanKhr
        };

        KhrVulkanEnable2? vulkan2Extension = null;
        string? vulkan2LoadError = null;
        try
        {
            if (!Api.TryGetInstanceExtension<KhrVulkanEnable2>(null, _instance, out var loadedVulkan2))
                vulkan2LoadError = "XR_KHR_vulkan_enable2 was not returned by the Silk.NET extension loader.";
            else
                vulkan2Extension = loadedVulkan2;
        }
        catch (Exception ex)
        {
            vulkan2LoadError = ex.Message;
        }

        KhrVulkanEnable? vulkanExtension = null;
        string? vulkanLoadError = null;
        if (vulkan2Extension is null)
        {
            try
            {
                if (!Api.TryGetInstanceExtension<KhrVulkanEnable>(null, _instance, out var loadedVulkan))
                    vulkanLoadError = "XR_KHR_vulkan_enable was not returned by the Silk.NET extension loader.";
                else
                    vulkanExtension = loadedVulkan;
            }
            catch (Exception ex)
            {
                vulkanLoadError = ex.Message;
            }
        }

        if (vulkan2Extension is not null)
        {
            if (vulkan2Extension.GetVulkanGraphicsRequirements2(_instance, _systemId, ref requirements) != Result.Success)
                throw new Exception("Failed to get Vulkan graphics requirements through XR_KHR_vulkan_enable2");
        }
        else if (vulkanExtension is not null)
        {
            if (vulkanExtension.GetVulkanGraphicsRequirements(_instance, _systemId, ref requirements) != Result.Success)
                throw new Exception("Failed to get Vulkan graphics requirements through XR_KHR_vulkan_enable");
        }
        else
        {
            throw new Exception($"Failed to get Vulkan OpenXR extension. XR_KHR_vulkan_enable2: {vulkan2LoadError ?? "<not tried>"}; XR_KHR_vulkan_enable: {vulkanLoadError ?? "<not tried>"}");
        }

        Debug.Vulkan($"Vulkan requirements: Min {requirements.MinApiVersionSupported}, Max {requirements.MaxApiVersionSupported}");

        if (Window.Renderer is not VulkanRenderer renderer)
            throw new Exception("Renderer is not a VulkanRenderer.");

        // Get the primary graphics queue family index
        var graphicsFamilyIndex = renderer.FamilyQueueIndices.GraphicsFamilyIndex!.Value;

        // Check if multiple graphics queues are supported
        bool supportsMultiQueue = renderer.SupportsMultipleGraphicsQueues();
        bool projectAllowsParallel = (RuntimeEngine.GameSettings as IVRGameStartupSettings)?.EnableOpenXrVulkanParallelRendering ?? true;

        if (supportsMultiQueue && projectAllowsParallel)
        {
            Debug.Vulkan("Multiple graphics queues are supported - enabling parallel eye rendering");
            _parallelRenderingEnabled = true;
        }
        else
        {
            if (!projectAllowsParallel)
                Debug.Vulkan("OpenXR Vulkan parallel eye rendering disabled by game startup settings.");
            else
                Debug.Vulkan("Multiple graphics queues not supported - using single queue rendering");

            _parallelRenderingEnabled = false;
        }

        GraphicsBindingVulkan2KHR vkBinding2 = default;
        GraphicsBindingVulkanKHR vkBinding = default;
        void* graphicsBinding = null;
        if (vulkan2Extension is not null)
        {
            vkBinding2 = new GraphicsBindingVulkan2KHR
            {
                Type = StructureType.GraphicsBindingVulkan2Khr,
                Instance = new(renderer.Instance.Handle),
                PhysicalDevice = new(renderer.PhysicalDevice.Handle),
                Device = new(renderer.Device.Handle),
                QueueFamilyIndex = graphicsFamilyIndex,
                QueueIndex = 0 // Main queue for session
            };
            graphicsBinding = &vkBinding2;
        }
        else
        {
            vkBinding = new GraphicsBindingVulkanKHR
            {
                Type = StructureType.GraphicsBindingVulkanKhr,
                Instance = new(renderer.Instance.Handle),
                PhysicalDevice = new(renderer.PhysicalDevice.Handle),
                Device = new(renderer.Device.Handle),
                QueueFamilyIndex = graphicsFamilyIndex,
                QueueIndex = 0 // Main queue for session
            };
            graphicsBinding = &vkBinding;
        }

        var createInfo = new SessionCreateInfo
        {
            Type = StructureType.SessionCreateInfo,
            SystemId = _systemId,
            Next = graphicsBinding
        };
        var result = CheckResult(Api.CreateSession(_instance, ref createInfo, ref _session), "xrCreateSession");
        if (result != Result.Success)
        {
            string bindingExtension = vulkan2Extension is not null ? "XR_KHR_vulkan_enable2" : "XR_KHR_vulkan_enable";
            string message = $"Failed to create Vulkan OpenXR session: {result}. " +
                $"BindingExtension={bindingExtension}, XrSystemId={_systemId}, " +
                $"VkInstance={renderer.Instance.Handle}, VkPhysicalDevice={renderer.PhysicalDevice.Handle}, VkDevice={renderer.Device.Handle}, " +
                $"QueueFamilyIndex={graphicsFamilyIndex}, QueueIndex=0, " +
                $"RuntimeMinVulkan={requirements.MinApiVersionSupported}, RuntimeMaxVulkan={requirements.MaxApiVersionSupported}.";

            if (result == Result.ErrorGraphicsDeviceInvalid)
                message += " The OpenXR runtime rejected the active Vulkan device; the Vulkan renderer must be created with the runtime-required OpenXR Vulkan instance/device extensions or through the XR_KHR_vulkan_enable2 creation path.";

            throw new OpenXrGraphicsSessionException(result, message);
        }
    }

    /// <summary>
    /// Initializes Vulkan swapchains for stereo rendering
    /// </summary>
    internal unsafe void InitializeVulkanSwapchains(VulkanRenderer renderer)
    {
        // Get view configuration
        var viewConfigType = ViewConfigurationType.PrimaryStereo;
        _viewCount = 0;
        Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, 0, ref _viewCount, null);

        if (_viewCount != 2)
        {
            throw new Exception($"Expected 2 views, got {_viewCount}");
        }

        _views = new View[_viewCount];

        fixed (ViewConfigurationView* viewConfigViewsPtr = _viewConfigViews)
        {
            Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, _viewCount, ref _viewCount, viewConfigViewsPtr);
        }

        // Create swapchains for each view
        for (int i = 0; i < _viewCount; i++)
        {
            var swapchainCreateInfo = new SwapchainCreateInfo
            {
                Type = StructureType.SwapchainCreateInfo,
                UsageFlags = SwapchainUsageFlags.ColorAttachmentBit,
                Format = 37 /* VK_FORMAT_R8G8B8A8_SRGB */,
                SampleCount = 1,
                Width = (uint)_viewConfigViews[i].RecommendedImageRectWidth,
                Height = (uint)_viewConfigViews[i].RecommendedImageRectHeight,
                FaceCount = 1,
                ArraySize = 1,
                MipCount = 1
            };

            fixed (Swapchain* swapchainPtr = &_swapchains[i])
            {
                if (Api.CreateSwapchain(_session, in swapchainCreateInfo, swapchainPtr) != Result.Success)
                {
                    throw new Exception($"Failed to create swapchain for view {i}");
                }
            }

            // Get swapchain images
            uint imageCount = 0;
            Api.EnumerateSwapchainImages(_swapchains[i], 0, &imageCount, null);

            _swapchainImagesVK[i] = (SwapchainImageVulkan2KHR*)Marshal.AllocHGlobal((int)imageCount * sizeof(SwapchainImageVulkan2KHR));

            for (uint j = 0; j < imageCount; j++)
                _swapchainImagesVK[i][j].Type = StructureType.SwapchainImageVulkan2Khr;

            Api.EnumerateSwapchainImages(_swapchains[i], imageCount, &imageCount, (SwapchainImageBaseHeader*)_swapchainImagesVK[i]);
            _swapchainImageCounts[i] = imageCount;
            RecordSmokeSwapchain(
                "Vulkan",
                i,
                swapchainCreateInfo.Width,
                swapchainCreateInfo.Height,
                swapchainCreateInfo.Format,
                swapchainCreateInfo.SampleCount,
                imageCount);

            Console.WriteLine($"Created swapchain {i} with {imageCount} images ({swapchainCreateInfo.Width}x{swapchainCreateInfo.Height})");
        }
        RecordSmokeSwapchainsCreated();
    }

}

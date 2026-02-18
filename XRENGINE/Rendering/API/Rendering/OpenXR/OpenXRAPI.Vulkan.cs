using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using System;
using System.Runtime.InteropServices;
using XREngine.Rendering.Vulkan;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

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

        if (!Api.TryGetInstanceExtension<KhrVulkanEnable>("", _instance, out var vulkanExtension))
            throw new Exception("Failed to get Vulkan extension");

        if (vulkanExtension.GetVulkanGraphicsRequirements(_instance, _systemId, ref requirements) != Result.Success)
            throw new Exception("Failed to get Vulkan graphics requirements");

        Debug.Vulkan($"Vulkan requirements: Min {requirements.MinApiVersionSupported}, Max {requirements.MaxApiVersionSupported}");

        if (Window.Renderer is not VulkanRenderer renderer)
            throw new Exception("Renderer is not a VulkanRenderer.");

        // Get the primary graphics queue family index
        var graphicsFamilyIndex = renderer.FamilyQueueIndices.GraphicsFamilyIndex!.Value;

        // Check if multiple graphics queues are supported
        bool supportsMultiQueue = renderer.SupportsMultipleGraphicsQueues();
        bool projectAllowsParallel = (Engine.GameSettings as IVRGameStartupSettings)?.EnableOpenXrVulkanParallelRendering ?? true;

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

        var vkBinding = new GraphicsBindingVulkanKHR
        {
            Type = StructureType.GraphicsBindingVulkanKhr,
            Instance = new(renderer.Instance.Handle),
            PhysicalDevice = new(renderer.PhysicalDevice.Handle),
            Device = new(renderer.Device.Handle),
            QueueFamilyIndex = graphicsFamilyIndex,
            QueueIndex = 0 // Main queue for session
        };
        var createInfo = new SessionCreateInfo
        {
            Type = StructureType.SessionCreateInfo,
            SystemId = _systemId,
            Next = &vkBinding
        };
        var result = CheckResult(Api.CreateSession(_instance, ref createInfo, ref _session), "xrCreateSession");
        if (result != Result.Success)
            throw new Exception($"Failed to create session: {result}");
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

            Console.WriteLine($"Created swapchain {i} with {imageCount} images ({swapchainCreateInfo.Width}x{swapchainCreateInfo.Height})");
        }
    }

}

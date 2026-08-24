using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

internal sealed partial class VulkanDeviceContext
{
    /// <summary>Enumerates native candidates after instance creation.</summary>
    public unsafe PhysicalDevice[] EnumeratePhysicalDevices(Vk api)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (!HasInstance)
            throw new InvalidOperationException("A Vulkan instance must exist before enumerating physical devices.");

        uint deviceCount = 0;
        if (api.EnumeratePhysicalDevices(Instance, ref deviceCount, null) != Result.Success)
            throw new InvalidOperationException("Failed to enumerate Vulkan physical-device count.");
        if (deviceCount == 0)
            throw new InvalidOperationException("Failed to find GPUs with Vulkan support.");

        PhysicalDevice[] devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devicesPtr = devices)
        {
            Result result = api.EnumeratePhysicalDevices(Instance, ref deviceCount, devicesPtr);
            if (result != Result.Success)
                throw new InvalidOperationException($"Failed to enumerate Vulkan physical devices. Result={result}.");
        }

        return devices;
    }

    /// <summary>
    /// Evaluates and atomically attaches a candidate using only composition-
    /// supplied output, integration, and OpenXR facts.
    /// </summary>
    public bool TrySelectPhysicalDevice(
        in VulkanPhysicalDeviceSelectionRequest request,
        out VulkanPhysicalDeviceSelectionResult result)
    {
        if (request.PhysicalDevice.Handle == 0)
            throw new ArgumentException("A valid physical device is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Capabilities);
        ArgumentNullException.ThrowIfNull(request.OutputProbe);
        ArgumentNullException.ThrowIfNull(request.ExtensionRequirements);
        if (!HasInstance)
            throw new InvalidOperationException("A Vulkan instance must exist before selecting a physical device.");
        if (PhysicalDevice.Handle != 0 || HasLogicalDevice)
            throw new InvalidOperationException("The Vulkan device context already owns a physical-device selection.");

        request.OutputRequirements.Validate();
        ValidateOutputRequirements(request.OutputRequirements);

        bool openXrRequestedDeviceMatched = request.OpenXrRequestedDevice.Matches(request.PhysicalDevice);
        if (!openXrRequestedDeviceMatched)
        {
            result = VulkanPhysicalDeviceSelectionResult.Rejected(
                request,
                openXrRequestedDeviceMatched,
                requiredExtensionsSupported: false,
                swapchainAdequate: false);
            return false;
        }

        bool extensionsSupported = VulkanPhysicalDevicePolicy.SupportsRequiredExtensions(
            request.Capabilities.AvailableExtensions,
            Configuration.RequiredDeviceExtensions.ToArray(),
            request.ExtensionRequirements.RequiredExtensions.ToArray());
        bool swapchainAdequate = !request.OutputRequirements.RequireSwapchainOutput ||
            VulkanPhysicalDevicePolicy.IsSwapchainAdequate(
                request.OutputProbe.SwapchainFormatCount,
                request.OutputProbe.SwapchainPresentModeCount);
        QueueFamilyIndices queueFamilies = VulkanQueueFamilySelector.Select(
            request.Capabilities.QueueFamilyArray,
            request.OutputProbe);
        bool queueFamiliesComplete = queueFamilies.IsComplete(request.OutputRequirements.RequirePresentQueue);
        bool isSuitable =
            request.OutputProbe.SurfaceCreated || !request.OutputRequirements.RequirePresentQueue
                ? queueFamiliesComplete && extensionsSupported && swapchainAdequate
                : false;
        bool supportsRayTracing = VulkanPhysicalDevicePolicy.SupportsRayTracing(
            request.Capabilities.AvailableExtensions);
        result = new(
            request.PhysicalDevice,
            request.Capabilities,
            queueFamilies,
            isSuitable,
            openXrRequestedDeviceMatched,
            extensionsSupported,
            swapchainAdequate,
            supportsRayTracing);
        if (!isSuitable)
            return false;

        AttachPhysicalDevice(request.PhysicalDevice, request.Capabilities, queueFamilies);
        return true;
    }

    private void ValidateOutputRequirements(in VulkanOutputDeviceRequirements requirements)
    {
        if (requirements.RequirePresentQueue != Configuration.RequirePresentQueue ||
            requirements.RequireSwapchainOutput != Configuration.RequireSwapchainOutput)
        {
            throw new InvalidOperationException(
                "Physical-device selection output requirements do not match the device-context lifetime policy.");
        }
    }
}

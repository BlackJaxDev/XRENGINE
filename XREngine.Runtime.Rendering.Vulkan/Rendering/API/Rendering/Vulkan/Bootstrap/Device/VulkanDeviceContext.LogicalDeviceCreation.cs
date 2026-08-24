using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

internal sealed partial class VulkanDeviceContext
{
    /// <summary>
    /// Creates, attaches, and resolves the complete native logical-device
    /// lifetime from already-negotiated feature-chain facts.
    /// </summary>
    private unsafe void CreateNativeLogicalDevice(
        Vk api,
        in NativeLogicalDeviceCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (request.QueueCreateInfos is null || request.QueueCreateInfoCount == 0)
            throw new ArgumentException("At least one Vulkan queue create-info is required.", nameof(request));
        if (request.FeatureChain is null)
            throw new ArgumentException("A Vulkan physical-device feature chain is required.", nameof(request));
        if (HasLogicalDevice)
            throw new InvalidOperationException("The Vulkan device context already owns a logical device.");

        using VulkanLogicalDeviceCreateInfoBuilder createInfoBuilder = new(
            request.QueueCreateInfos,
            request.QueueCreateInfoCount,
            request.FeatureChain,
            request.EnabledExtensions);
        DeviceCreateInfo createInfo = createInfoBuilder.CreateInfo;
        var getInstanceProcAddr = api.GetInstanceProcAddr(default, "vkGetInstanceProcAddr");

        Device createdDevice;
        bool createdThroughOpenXr;
        if (OpenXrBootstrapContext is not null)
        {
            if (!OpenXrBootstrapContext.TryCreateVulkanDevice(
                    (nint)PhysicalDevice.Handle,
                    &createInfo,
                    getInstanceProcAddr,
                    out nint openXrCreatedDeviceHandle,
                    out _,
                    out string? openXrCreateFailure))
            {
                throw new InvalidOperationException(
                    $"Failed to create Vulkan logical device through OpenXR: {openXrCreateFailure}");
            }

            createdDevice = new Device(openXrCreatedDeviceHandle);
            createdThroughOpenXr = true;
        }
        else
        {
            Result createResult = api.CreateDevice(PhysicalDevice, in createInfo, null, out createdDevice);
            if (createResult != Result.Success)
                throw new InvalidOperationException($"Failed to create Vulkan logical device. Result={createResult}");

            createdThroughOpenXr = false;
        }

        try
        {
            AttachDevice(
                createdDevice,
                createdThroughOpenXr,
                request.EnabledExtensionSet);
            ResolveQueues(api, request.SupportsMultipleGraphicsQueues);
        }
        catch
        {
            api.DestroyDevice(createdDevice, null);
            throw;
        }

    }

    /// <summary>
    /// Stack-scoped native pointer contract used only while building
    /// <c>VkDeviceCreateInfo</c>. No pointer escapes device bootstrap.
    /// </summary>
    private readonly unsafe ref struct NativeLogicalDeviceCreateRequest
    {
        internal NativeLogicalDeviceCreateRequest(
            DeviceQueueCreateInfo* queueCreateInfos,
            uint queueCreateInfoCount,
            void* featureChain,
            string[] enabledExtensions,
            VulkanDeviceExtensionSet enabledExtensionSet,
            bool supportsMultipleGraphicsQueues)
        {
            QueueCreateInfos = queueCreateInfos;
            QueueCreateInfoCount = queueCreateInfoCount;
            FeatureChain = featureChain;
            EnabledExtensions = enabledExtensions;
            EnabledExtensionSet = enabledExtensionSet;
            SupportsMultipleGraphicsQueues = supportsMultipleGraphicsQueues;
        }

        internal DeviceQueueCreateInfo* QueueCreateInfos { get; }
        internal uint QueueCreateInfoCount { get; }
        internal void* FeatureChain { get; }
        internal string[] EnabledExtensions { get; }
        internal VulkanDeviceExtensionSet EnabledExtensionSet { get; }
        internal bool SupportsMultipleGraphicsQueues { get; }
    }
}

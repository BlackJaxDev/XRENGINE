using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private bool _supportsAnisotropy;
    private Device device;
    private Queue graphicsQueue;
    private Queue presentQueue;

    public Device Device => device;
    public Queue GraphicsQueue => graphicsQueue;
    public Queue PresentQueue => presentQueue;

    private void DestroyLogicalDevice()
        => Api!.DestroyDevice(device, null);

    /// <summary>
    /// Creates a logical device interface to the physical device with specific 
    /// queue families and extensions.
    /// </summary>
    /// <remarks>
    /// The logical device is the primary interface for an application to the GPU.
    /// This method:
    /// 1. Identifies required queue families (graphics and presentation)
    /// 2. Sets up queue creation information
    /// 3. Configures device features
    /// 4. Enables required device extensions
    /// 5. Enables validation layers if needed
    /// 6. Creates the device and obtains queue handles
    /// </remarks>
    private void CreateLogicalDevice()
    {
        // Get queue family indices required for rendering and presentation
        var indices = FamilyQueueIndices;

        // Create an array of queue family indices needed by this device
        var uniqueQueueFamilies = new[] { indices.GraphicsFamilyIndex!.Value, indices.PresentFamilyIndex!.Value };
        // Remove duplicates (graphics and present queue might be the same family)
        uniqueQueueFamilies = [.. uniqueQueueFamilies.Distinct()];

        // Allocate memory for queue create infos
        using var mem = GlobalMemory.Allocate(uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo));
        var queueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());

        // Configure queue priority (1.0 = highest priority)
        float queuePriority = 1.0f;
        // Set up creation info for each queue family
        for (int i = 0; i < uniqueQueueFamilies.Length; i++)
            queueCreateInfos[i] = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueQueueFamilies[i],
                QueueCount = 1, // Create one queue per family
                PQueuePriorities = &queuePriority
            };

        // Specify device features to enable (none specifically enabled here)
        PhysicalDeviceFeatures supportedFeatures = new();
        Api!.GetPhysicalDeviceFeatures(_physicalDevice, out supportedFeatures);

        PhysicalDeviceFeatures deviceFeatures = new();
        if (supportedFeatures.SamplerAnisotropy)
        {
            deviceFeatures.SamplerAnisotropy = Vk.True;
            _supportsAnisotropy = true;
        }

        // Configure the logical device creation
        DeviceCreateInfo createInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,

            PEnabledFeatures = &deviceFeatures,

            // Enable required device extensions (e.g., swapchain)
            EnabledExtensionCount = (uint)deviceExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions)
        };

        // Enable validation layers if debugging is enabled
        if (EnableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);
        }
        else
            createInfo.EnabledLayerCount = 0;

        // Create the logical device
        if (Api!.CreateDevice(_physicalDevice, in createInfo, null, out device) != Result.Success)
            throw new Exception("Failed to create logical device.");

        // Retrieve handles to the queues we need
        Api!.GetDeviceQueue(device, indices.GraphicsFamilyIndex!.Value, 0, out graphicsQueue);
        Api!.GetDeviceQueue(device, indices.PresentFamilyIndex!.Value, 0, out presentQueue);

        // Clean up allocated memory for validation layer names
        if (EnableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

        // Clean up allocated memory for extension names
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
    }
}
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Performs physical-device discovery only. It does not decide which optional
/// features or extensions the engine will enable.
/// </summary>
internal static unsafe class VulkanDeviceCapabilityQuery
{
    public static VulkanPhysicalDeviceCapabilitySnapshot Query(Vk api, PhysicalDevice physicalDevice)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (physicalDevice.Handle == 0)
            throw new ArgumentException("A valid physical device is required.", nameof(physicalDevice));

        api.GetPhysicalDeviceFeatures(physicalDevice, out PhysicalDeviceFeatures coreFeatures);
        api.GetPhysicalDeviceProperties(physicalDevice, out PhysicalDeviceProperties properties);

        uint queueFamilyCount = 0;
        api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilyCount, null);
        QueueFamilyProperties[] queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
            api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilyCount, queueFamiliesPtr);

        return new VulkanPhysicalDeviceCapabilitySnapshot(
            coreFeatures,
            properties,
            queueFamilies,
            EnumerateExtensions(api, physicalDevice));
    }

    public static VulkanDeviceExtensionSet EnumerateExtensions(Vk api, PhysicalDevice physicalDevice)
    {
        if (physicalDevice.Handle == 0)
            return VulkanDeviceExtensionSet.Empty;

        uint extensionCount = 0;
        api.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, ref extensionCount, null);
        if (extensionCount == 0)
            return VulkanDeviceExtensionSet.Empty;

        ExtensionProperties[] properties = new ExtensionProperties[extensionCount];
        string[] names = new string[extensionCount];
        int nameCount = 0;
        fixed (ExtensionProperties* propertiesPtr = properties)
        {
            api.EnumerateDeviceExtensionProperties(physicalDevice, (byte*)null, ref extensionCount, propertiesPtr);
            for (int i = 0; i < properties.Length; i++)
            {
                string? name = SilkMarshal.PtrToString((nint)propertiesPtr[i].ExtensionName);
                if (!string.IsNullOrWhiteSpace(name))
                    names[nameCount++] = name;
            }
        }

        return nameCount == names.Length
            ? new VulkanDeviceExtensionSet(names)
            : new VulkanDeviceExtensionSet(names.AsSpan(0, nameCount).ToArray());
    }
}

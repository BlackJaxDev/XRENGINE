using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns transient native extension-name storage while assembling
/// <see cref="DeviceCreateInfo"/>.
/// </summary>
internal unsafe ref struct VulkanLogicalDeviceCreateInfoBuilder
{
    private nint _extensionNames;

    public VulkanLogicalDeviceCreateInfoBuilder(
        DeviceQueueCreateInfo* queueCreateInfos,
        uint queueCreateInfoCount,
        void* featureChain,
        string[] enabledExtensions)
    {
        _extensionNames = SilkMarshal.StringArrayToPtr(enabledExtensions);
        CreateInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = queueCreateInfoCount,
            PQueueCreateInfos = queueCreateInfos,
            PNext = featureChain,
            PEnabledFeatures = null,
            EnabledExtensionCount = (uint)enabledExtensions.Length,
            PpEnabledExtensionNames = (byte**)_extensionNames,
            EnabledLayerCount = 0,
            PpEnabledLayerNames = null,
        };
    }

    public DeviceCreateInfo CreateInfo { get; }

    public void Dispose()
    {
        nint extensionNames = _extensionNames;
        _extensionNames = 0;
        if (extensionNames != 0)
            SilkMarshal.Free(extensionNames);
    }
}

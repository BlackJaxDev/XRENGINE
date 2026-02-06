using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using XREngine;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private PhysicalDevice _physicalDevice;
    public PhysicalDevice PhysicalDevice => _physicalDevice;

    private void PickPhysicalDevice()
    {
        uint devicedCount = 0;
        Api!.EnumeratePhysicalDevices(instance, ref devicedCount, null);

        if (devicedCount == 0)
            throw new Exception("Failed to find GPUs with Vulkan support.");
        
        var devices = new PhysicalDevice[devicedCount];
        fixed (PhysicalDevice* devicesPtr = devices)
        {
            Api!.EnumeratePhysicalDevices(instance, ref devicedCount, devicesPtr);
        }

        foreach (var device in devices)
            if (IsDeviceSuitable(device, out var indices))
            {
                _physicalDevice = device;
                _familyQueueIndicesCache = indices;
                break;
            }
        
        if (_physicalDevice.Handle == 0)
            throw new Exception("Failed to find a suitable GPU for Vulkan.");

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
        // NVIDIA PCI vendor ID.
        Engine.Rendering.State.IsNVIDIA = properties.VendorID == 0x10DE;
        // Intel PCI vendor ID.
        Engine.Rendering.State.IsIntel = properties.VendorID == 0x8086;
        Engine.Rendering.State.IsVulkan = true;

        // Cache Vulkan ray tracing extension availability once at startup.
        Engine.Rendering.State.HasVulkanRayTracing = ProbeVulkanRayTracingSupport(_physicalDevice);
    }

    private bool ProbeVulkanRayTracingSupport(PhysicalDevice device)
    {
        try
        {
            uint extensionsCount = 0;
            Api!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extensionsCount, null);

            var availableExtensions = new ExtensionProperties[extensionsCount];
            fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
            {
                Api!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extensionsCount, availableExtensionsPtr);
            }

            var availableExtensionNames = availableExtensions
                .Select(static extension => Marshal.PtrToStringAnsi((IntPtr)extension.ExtensionName))
                .Where(static n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.Ordinal);

            // Prefer KHR ray tracing pipeline; fall back to legacy NV extension if present.
            bool hasKhrRt =
                availableExtensionNames.Contains("VK_KHR_ray_tracing_pipeline") &&
                availableExtensionNames.Contains("VK_KHR_acceleration_structure") &&
                availableExtensionNames.Contains("VK_KHR_deferred_host_operations");
            bool hasNvRt = availableExtensionNames.Contains("VK_NV_ray_tracing");

            bool supported = hasKhrRt || hasNvRt;

            Debug.Vulkan(supported
                ? "Vulkan ray tracing extensions: available"
                : "Vulkan ray tracing extensions: not reported; RT features will remain disabled.");

            return supported;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"Failed to query Vulkan ray tracing extensions: {ex.Message}");
            return false;
        }
    }

    private bool IsDeviceSuitable(PhysicalDevice device, out QueueFamilyIndices indices)
    {
        indices = FindQueueFamilies(device);

        bool extensionsSupported = CheckDeviceExtensionsSupport(device);

        bool swapChainAdequate = false;
        if (extensionsSupported)
        {
            var swapChainSupport = QuerySwapChainSupport(device);
            swapChainAdequate = 
                swapChainSupport.Formats.Length != 0 &&
                swapChainSupport.PresentModes.Length != 0;
        }

        return indices.IsComplete() && extensionsSupported && swapChainAdequate;
    }

    private bool CheckDeviceExtensionsSupport(PhysicalDevice device)
    {
        uint extentionsCount = 0;
        Api!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extentionsCount, null);

        var availableExtensions = new ExtensionProperties[extentionsCount];
        fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
        {
            Api!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extentionsCount, availableExtensionsPtr);
        }

        var availableExtensionNames = availableExtensions.Select(extension => Marshal.PtrToStringAnsi((IntPtr)extension.ExtensionName)).ToHashSet();

        return deviceExtensions.All(availableExtensionNames.Contains);

    }

    public uint FindMemoryType(uint typeFilter, MemoryPropertyFlags memProps)
    {
        Api!.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProperties);

        for (int i = 0; i < memProperties.MemoryTypeCount; i++)
            if ((typeFilter & (1 << i)) != 0 && (memProperties.MemoryTypes[i].PropertyFlags & memProps) == memProps)
                return (uint)i;

        throw new Exception("Failed to find suitable memory type.");
    }
}

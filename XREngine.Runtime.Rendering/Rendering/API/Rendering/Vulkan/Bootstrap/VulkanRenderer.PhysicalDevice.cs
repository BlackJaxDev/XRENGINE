using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using XREngine;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private PhysicalDevice _physicalDevice;
    public PhysicalDevice PhysicalDevice => _physicalDevice;
    private ulong _nonCoherentAtomSize = 1;
    private ulong _uniformBufferOffsetAlignment = 1;

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

        nint openXrRequestedDeviceHandle;
        string? openXrDeviceQueryFailure;
        bool hasOpenXrRequestedDevice;
        if (_openXrVulkanEnable2Context is not null)
        {
            hasOpenXrRequestedDevice = _openXrVulkanEnable2Context.TryGetRequestedVulkanPhysicalDevice(
                (nint)instance.Handle,
                out openXrRequestedDeviceHandle,
                out openXrDeviceQueryFailure);
        }
        else
        {
            hasOpenXrRequestedDevice = OpenXRAPI.TryGetRequestedVulkanPhysicalDevice(
                (nint)instance.Handle,
                out openXrRequestedDeviceHandle,
                out openXrDeviceQueryFailure);
        }
        if (!hasOpenXrRequestedDevice && !string.IsNullOrWhiteSpace(openXrDeviceQueryFailure))
            throw new Exception($"Failed to query the OpenXR runtime-selected Vulkan physical device: {openXrDeviceQueryFailure}");

        foreach (var device in devices)
        {
            if (hasOpenXrRequestedDevice && (nint)device.Handle != openXrRequestedDeviceHandle)
                continue;

            if (IsDeviceSuitable(device, out var indices))
            {
                _physicalDevice = device;
                _familyQueueIndicesCache = indices;
                break;
            }
        }
        
        if (_physicalDevice.Handle == 0)
        {
            if (hasOpenXrRequestedDevice)
                throw new Exception($"The OpenXR runtime-selected Vulkan physical device 0x{(nuint)openXrRequestedDeviceHandle:X} is not suitable for this Vulkan renderer/window surface.");

            throw new Exception("Failed to find a suitable GPU for Vulkan.");
        }

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
        if (hasOpenXrRequestedDevice)
        {
            string deviceName = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "<unknown>";
            Debug.Vulkan(
                "[OpenXR] Using runtime-selected Vulkan physical device: {0} vendor=0x{1:X} device=0x{2:X} handle=0x{3:X}",
                deviceName,
                properties.VendorID,
                properties.DeviceID,
                (nuint)_physicalDevice.Handle);
        }

        _nonCoherentAtomSize = System.Math.Max(properties.Limits.NonCoherentAtomSize, 1UL);
        _uniformBufferOffsetAlignment = System.Math.Max(properties.Limits.MinUniformBufferOffsetAlignment, 1UL);
        // NVIDIA PCI vendor ID.
        RuntimeEngine.Rendering.State.IsNVIDIA = properties.VendorID == 0x10DE;
        // Intel PCI vendor ID.
        RuntimeEngine.Rendering.State.IsIntel = properties.VendorID == 0x8086;
        RuntimeEngine.Rendering.State.IsVulkan = true;
        RuntimeEngine.Rendering.State.SupportsOpenGLLayeredFramebuffers = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderLayeredRendering = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderLayeredRendering = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLViewportArray = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLVertexShaderViewportIndex = false;
        RuntimeEngine.Rendering.State.SupportsOpenGLGeometryShaderViewportIndex = false;
        RuntimeEngine.Rendering.State.MaxOpenGLViewports = 1;
        RuntimeEngine.Rendering.State.VulkanDeviceName = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)properties.DeviceName);
        RuntimeEngine.Rendering.State.VulkanVendorId = properties.VendorID;
        RuntimeEngine.Rendering.State.VulkanDeviceId = properties.DeviceID;

        // Cache Vulkan ray tracing extension availability once at startup.
        RuntimeEngine.Rendering.State.HasVulkanRayTracing = ProbeVulkanRayTracingSupport(_physicalDevice);
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

        return _requiredDeviceExtensions.All(availableExtensionNames.Contains);

    }

    public uint FindMemoryType(uint typeFilter, MemoryPropertyFlags memProps)
    {
        Api!.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProperties);

        for (int i = 0; i < memProperties.MemoryTypeCount; i++)
            if ((typeFilter & (1 << i)) != 0 && (memProperties.MemoryTypes[i].PropertyFlags & memProps) == memProps)
                return (uint)i;

        throw new Exception("Failed to find suitable memory type.");
    }

    public bool TryFindMemoryType(uint typeFilter, MemoryPropertyFlags memProps, out uint memoryTypeIndex)
    {
        Api!.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProperties);

        for (int i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1 << i)) == 0)
                continue;

            if ((memProperties.MemoryTypes[i].PropertyFlags & memProps) != memProps)
                continue;

            memoryTypeIndex = (uint)i;
            return true;
        }

        memoryTypeIndex = 0;
        return false;
    }
}

using Silk.NET.Vulkan;
using XREngine;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private PhysicalDevice _physicalDevice;
    private VulkanPhysicalDeviceCapabilitySnapshot? _physicalDeviceCapabilitySnapshot;
    public PhysicalDevice PhysicalDevice => _physicalDevice;
    private ulong _nonCoherentAtomSize = 1;
    internal ulong _uniformBufferOffsetAlignment = 1;

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

            VulkanPhysicalDeviceCapabilitySnapshot snapshot =
                VulkanDeviceCapabilityQuery.Query(Api!, device);
            if (IsDeviceSuitable(device, snapshot, out var indices))
            {
                _physicalDevice = device;
                _deviceContext.AttachPhysicalDevice(device);
                _physicalDeviceCapabilitySnapshot = snapshot;
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
        RuntimeEngine.Rendering.State.HasVulkanRayTracing =
            ProbeVulkanRayTracingSupport(_physicalDeviceCapabilitySnapshot!);
    }

    private static bool ProbeVulkanRayTracingSupport(
        VulkanPhysicalDeviceCapabilitySnapshot snapshot)
    {
        bool supported =
            VulkanPhysicalDevicePolicy.SupportsRayTracing(snapshot.AvailableExtensions);
        Debug.Vulkan(supported
            ? "Vulkan ray tracing extensions: available"
            : "Vulkan ray tracing extensions: not reported; RT features will remain disabled.");
        return supported;
    }

    private bool IsDeviceSuitable(
        PhysicalDevice device,
        VulkanPhysicalDeviceCapabilitySnapshot snapshot,
        out QueueFamilyIndices indices)
    {
        indices = VulkanQueueFamilySelector.Select(
            snapshot.QueueFamilyArray,
            _targetDriver.RequiresPresentQueue ? khrSurface : null,
            device,
            surface);
        bool extensionsSupported =
            VulkanPhysicalDevicePolicy.SupportsRequiredExtensions(
                snapshot.AvailableExtensions,
                _requiredDeviceExtensions,
                _streamlineRequiredDeviceExtensions);

        bool finalOutputAdequate = extensionsSupported;
        if (extensionsSupported && _targetDriver.RequiresSwapchainOutput)
        {
            var swapChainSupport = QuerySwapChainSupport(device);
            finalOutputAdequate = VulkanPhysicalDevicePolicy.IsSwapchainAdequate(
                swapChainSupport.Formats.Length,
                swapChainSupport.PresentModes.Length);
        }

        return indices.IsComplete(_targetDriver.RequiresPresentQueue) &&
            extensionsSupported &&
            finalOutputAdequate;
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

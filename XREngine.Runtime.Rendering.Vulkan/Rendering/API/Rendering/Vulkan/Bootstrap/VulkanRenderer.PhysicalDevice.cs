using Silk.NET.Core;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public PhysicalDevice PhysicalDevice => _deviceContext.PhysicalDevice;

    private void PublishPresentationSupportProbe()
    {
        if (!OutputRuntime.TargetDriver.RequiresPresentQueue)
            return;

        Silk.NET.Vulkan.Extensions.KHR.KhrSurface surfaceApi = _outputRuntime.SurfaceApi
            ?? throw new InvalidOperationException("The Vulkan target did not publish a surface API before physical-device selection.");
        SurfaceKHR presentationSurface = _outputRuntime.Surface;
        if (presentationSurface.Handle == 0)
            throw new InvalidOperationException("The Vulkan target did not publish a surface before physical-device selection.");

        _deviceContext.AttachPresentationSupportProbe((
            PhysicalDevice physicalDevice,
            uint queueFamilyIndex,
            out bool supportsPresentation) =>
        {
            Result result = surfaceApi.GetPhysicalDeviceSurfaceSupport(
                physicalDevice,
                queueFamilyIndex,
                presentationSurface,
                out Bool32 presentSupport);
            supportsPresentation = presentSupport;
            return result;
        });
    }

    private void PickPhysicalDevice()
    {
        uint devicedCount = 0;
        Api!.EnumeratePhysicalDevices(_deviceContext.Instance, ref devicedCount, null);

        if (devicedCount == 0)
            throw new Exception("Failed to find GPUs with Vulkan support.");
        
        var devices = new PhysicalDevice[devicedCount];
        fixed (PhysicalDevice* devicesPtr = devices)
        {
            Api!.EnumeratePhysicalDevices(_deviceContext.Instance, ref devicedCount, devicesPtr);
        }

        nint openXrRequestedDeviceHandle;
        string? openXrDeviceQueryFailure;
        bool hasOpenXrRequestedDevice;
        if (_deviceContext.OpenXrBootstrapContext is not null)
        {
            hasOpenXrRequestedDevice = _deviceContext.OpenXrBootstrapContext.TryGetRequestedVulkanPhysicalDevice(
                (nint)_deviceContext.Instance.Handle,
                out openXrRequestedDeviceHandle,
                out openXrDeviceQueryFailure);
        }
        else
        {
            hasOpenXrRequestedDevice = OpenXRAPI.TryGetRequestedVulkanPhysicalDevice(
                (nint)_deviceContext.Instance.Handle,
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
            if (IsDeviceSuitable(device, snapshot, out QueueFamilyIndices indices))
            {
                _deviceContext.AttachPhysicalDevice(device, snapshot, indices);
                break;
            }
        }
        
        if (_deviceContext.PhysicalDevice.Handle == 0)
        {
            if (hasOpenXrRequestedDevice)
                throw new Exception($"The OpenXR runtime-selected Vulkan physical device 0x{(nuint)openXrRequestedDeviceHandle:X} is not suitable for this Vulkan renderer/window surface.");

            throw new Exception("Failed to find a suitable GPU for Vulkan.");
        }

        Api!.GetPhysicalDeviceProperties(_deviceContext.PhysicalDevice, out var properties);
        if (hasOpenXrRequestedDevice)
        {
            string deviceName = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "<unknown>";
            Debug.Vulkan(
                "[OpenXR] Using runtime-selected Vulkan physical device: {0} vendor=0x{1:X} device=0x{2:X} handle=0x{3:X}",
                deviceName,
                properties.VendorID,
                properties.DeviceID,
                (nuint)_deviceContext.PhysicalDevice.Handle);
        }

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
            ProbeVulkanRayTracingSupport(_deviceContext.PhysicalDeviceCapabilities!);
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
        indices = _deviceContext.SelectQueueFamilies(device, snapshot);
        bool extensionsSupported = _deviceContext.SupportsRequiredDeviceExtensions(
            snapshot.AvailableExtensions,
            _outputRuntime._streamlineRequiredDeviceExtensions) &&
            _deviceContext.SupportsRequiredDeviceExtensions(
                snapshot.AvailableExtensions,
                OpenXRAPI.GetRequestedVulkanRuntimeRequirements().DeviceExtensions);

        bool finalOutputAdequate = extensionsSupported;
        if (extensionsSupported && OutputRuntime.TargetDriver.RequiresSwapchainOutput)
        {
            var swapChainSupport = QuerySwapChainSupport(device);
            finalOutputAdequate = VulkanPhysicalDevicePolicy.IsSwapchainAdequate(
                swapChainSupport.Formats.Length,
                swapChainSupport.PresentModes.Length);
        }

        return indices.IsComplete(OutputRuntime.TargetDriver.RequiresPresentQueue) &&
            extensionsSupported &&
            finalOutputAdequate;
    }

    public uint FindMemoryType(uint typeFilter, MemoryPropertyFlags memProps)
    {
        Api!.GetPhysicalDeviceMemoryProperties(_deviceContext.PhysicalDevice, out PhysicalDeviceMemoryProperties memProperties);

        for (int i = 0; i < memProperties.MemoryTypeCount; i++)
            if ((typeFilter & (1 << i)) != 0 && (memProperties.MemoryTypes[i].PropertyFlags & memProps) == memProps)
                return (uint)i;

        throw new Exception("Failed to find suitable memory type.");
    }

    public bool TryFindMemoryType(uint typeFilter, MemoryPropertyFlags memProps, out uint memoryTypeIndex)
    {
        Api!.GetPhysicalDeviceMemoryProperties(_deviceContext.PhysicalDevice, out PhysicalDeviceMemoryProperties memProperties);

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

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.NV;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Owns device-scoped extension command tables. Loading occurs once immediately
/// after logical-device creation and the table is cleared with the device.
/// </summary>
internal sealed class VulkanDeviceExtensionFunctions
{
    public KhrDrawIndirectCount? KhrDrawIndirectCount { get; private set; }
    public KhrDynamicRendering? KhrDynamicRendering { get; private set; }
    public KhrSynchronization2? KhrSynchronization2 { get; private set; }
    public ExtMeshShader? ExtMeshShader { get; private set; }
    public ExtTransformFeedback? ExtTransformFeedback { get; private set; }
    public KhrExternalMemoryWin32? KhrExternalMemoryWin32 { get; private set; }
    public KhrExternalSemaphoreWin32? KhrExternalSemaphoreWin32 { get; private set; }
    public NVMemoryDecompression? NvMemoryDecompression { get; private set; }
    public NVCopyMemoryIndirect? NvCopyMemoryIndirect { get; private set; }
    public ExtDeviceFault? ExtDeviceFault { get; private set; }
    public NVDeviceDiagnosticCheckpoints? NvDeviceDiagnosticCheckpoints { get; private set; }

    public void Load(
        Vk api,
        Instance instance,
        Device device,
        IReadOnlySet<string> enabledExtensions)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(enabledExtensions);
        if (device.Handle == 0)
            throw new ArgumentException("A valid logical device is required.", nameof(device));

        Clear();

        KhrDrawIndirectCount? drawIndirectCount = null;
        if (enabledExtensions.Contains("VK_KHR_draw_indirect_count"))
            api.TryGetDeviceExtension(instance, device, out drawIndirectCount);
        KhrDrawIndirectCount = drawIndirectCount;

        KhrDynamicRendering? dynamicRendering = null;
        if (enabledExtensions.Contains("VK_KHR_dynamic_rendering"))
            api.TryGetDeviceExtension(instance, device, out dynamicRendering);
        KhrDynamicRendering = dynamicRendering;

        KhrSynchronization2? synchronization2 = null;
        if (enabledExtensions.Contains("VK_KHR_synchronization2"))
            api.TryGetDeviceExtension(instance, device, out synchronization2);
        KhrSynchronization2 = synchronization2;

        ExtMeshShader? meshShader = null;
        if (enabledExtensions.Contains(ExtMeshShader.ExtensionName))
            api.TryGetDeviceExtension(instance, device, out meshShader);
        ExtMeshShader = meshShader;

        ExtTransformFeedback? transformFeedback = null;
        if (enabledExtensions.Contains(ExtTransformFeedback.ExtensionName))
            api.TryGetDeviceExtension(instance, device, out transformFeedback);
        ExtTransformFeedback = transformFeedback;

        KhrExternalMemoryWin32? externalMemoryWin32 = null;
        if (enabledExtensions.Contains("VK_KHR_external_memory_win32"))
            api.TryGetDeviceExtension(instance, device, out externalMemoryWin32);
        KhrExternalMemoryWin32 = externalMemoryWin32;

        KhrExternalSemaphoreWin32? externalSemaphoreWin32 = null;
        if (enabledExtensions.Contains("VK_KHR_external_semaphore_win32"))
            api.TryGetDeviceExtension(instance, device, out externalSemaphoreWin32);
        KhrExternalSemaphoreWin32 = externalSemaphoreWin32;

        NVMemoryDecompression? memoryDecompression = null;
        if (enabledExtensions.Contains("VK_NV_memory_decompression"))
            api.TryGetDeviceExtension(instance, device, out memoryDecompression);
        NvMemoryDecompression = memoryDecompression;

        NVCopyMemoryIndirect? copyMemoryIndirect = null;
        if (enabledExtensions.Contains("VK_NV_copy_memory_indirect"))
            api.TryGetDeviceExtension(instance, device, out copyMemoryIndirect);
        NvCopyMemoryIndirect = copyMemoryIndirect;

        ExtDeviceFault? deviceFault = null;
        if (enabledExtensions.Contains("VK_EXT_device_fault"))
            api.TryGetDeviceExtension(instance, device, out deviceFault);
        ExtDeviceFault = deviceFault;

        NVDeviceDiagnosticCheckpoints? diagnosticCheckpoints = null;
        if (enabledExtensions.Contains("VK_NV_device_diagnostic_checkpoints"))
            api.TryGetDeviceExtension(instance, device, out diagnosticCheckpoints);
        NvDeviceDiagnosticCheckpoints = diagnosticCheckpoints;
    }

    public void Clear()
    {
        KhrDrawIndirectCount = null;
        KhrDynamicRendering = null;
        KhrSynchronization2 = null;
        ExtMeshShader = null;
        ExtTransformFeedback = null;
        KhrExternalMemoryWin32 = null;
        KhrExternalSemaphoreWin32 = null;
        NvMemoryDecompression = null;
        NvCopyMemoryIndirect = null;
        ExtDeviceFault = null;
        NvDeviceDiagnosticCheckpoints = null;
    }
}

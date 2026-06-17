using XREngine.Rendering;

namespace XREngine;

internal sealed class RuntimeVulkanRobustnessSettings
{
    private EVulkanAllocatorBackend _allocatorBackend = EVulkanAllocatorBackend.Vma;
    private EVulkanSynchronizationBackend _synchronizationBackend = EVulkanSynchronizationBackend.Sync2;
    private EVulkanDescriptorUpdateBackend _descriptorUpdateBackend = EVulkanDescriptorUpdateBackend.Template;
    private bool _dynamicUniformBufferEnabled = true;

    public EVulkanAllocatorBackend AllocatorBackend
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.VulkanAllocatorBackend
            : _allocatorBackend;
        set => _allocatorBackend = value;
    }

    public EVulkanSynchronizationBackend SynchronizationBackend
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.VulkanSynchronizationBackend
            : _synchronizationBackend;
        set => _synchronizationBackend = value;
    }

    public EVulkanSynchronizationBackend SyncBackend
    {
        get => SynchronizationBackend;
        set => SynchronizationBackend = value;
    }

    public EVulkanDescriptorUpdateBackend DescriptorUpdateBackend
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.VulkanDescriptorUpdateBackend
            : _descriptorUpdateBackend;
        set => _descriptorUpdateBackend = value;
    }

    public bool DynamicUniformBufferEnabled
    {
        get => TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
            ? services.VulkanDynamicUniformBufferEnabled
            : _dynamicUniformBufferEnabled;
        set => _dynamicUniformBufferEnabled = value;
    }

    public bool EnableDebugNames { get; set; }
    public bool EnableValidationLayers { get; set; }

    private static bool TryGetHostRuntimeSettings(out IRuntimeRenderingHostServices services)
    {
        services = RuntimeRenderingHostServices.Current;
        return RuntimeRenderingHostServices.HasConcreteHost;
    }
}

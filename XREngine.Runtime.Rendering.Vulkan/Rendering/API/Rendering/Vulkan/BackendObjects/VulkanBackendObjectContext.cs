using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow device and identity services shared by backend wrappers from one
/// renderer generation.
/// </summary>
internal sealed class VulkanBackendObjectContext(
    Vk api,
    VulkanDeviceContext? deviceContext,
    VulkanBackendObjectRegistry registry,
    VulkanResourceLifetimeTracker lifetime,
    VulkanDescriptorManager descriptors,
    VulkanPipelineManager pipelines)
{
    private VulkanDeviceContext? _deviceContext = deviceContext;

    public Vk Api { get; } = api;
    public Device Device => RequireDeviceContext().Device;
    public PhysicalDevice PhysicalDevice => RequireDeviceContext().PhysicalDevice;
    public bool IsLogicalDeviceReady => _deviceContext?.IsReady == true;
    public bool IsDeviceOperational => _deviceContext?.IsOperational == true;
    public VulkanBackendObjectRegistry Registry { get; } = registry;
    public VulkanBindingAllocator BindingAllocator => Registry.BindingAllocator;
    public VulkanResourceLifetimeTracker Lifetime { get; } = lifetime;
    public VulkanDescriptorManager Descriptors { get; } = descriptors;
    public VulkanPipelineManager Pipelines { get; } = pipelines;

    /// <summary>
    /// Completes staged bootstrap when wrappers are requested by the base renderer
    /// constructor before the Vulkan device authority has been created.
    /// </summary>
    public void PublishDeviceContext(VulkanDeviceContext deviceContext)
    {
        ArgumentNullException.ThrowIfNull(deviceContext);
        VulkanDeviceContext? current = Interlocked.CompareExchange(
            ref _deviceContext,
            deviceContext,
            comparand: null);
        if (current is not null && !ReferenceEquals(current, deviceContext))
            throw new InvalidOperationException("The Vulkan backend object context already owns a different device context.");
    }

    private VulkanDeviceContext RequireDeviceContext()
        => _deviceContext
            ?? throw new InvalidOperationException("The Vulkan device context has not been published to backend objects yet.");
}

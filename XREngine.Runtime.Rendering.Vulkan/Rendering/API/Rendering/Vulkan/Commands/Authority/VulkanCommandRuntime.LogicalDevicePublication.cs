using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private int _logicalDevicePublicationApplied;

    /// <summary>
    /// Applies and verifies the immutable command-dispatch selection emitted by
    /// Device bootstrap. Native entry points remain owned by DeviceContext.
    /// </summary>
    internal void ApplyLogicalDevicePublication(
        in VulkanLogicalDeviceBootstrapResult.CommandPublication publication)
    {
        if (Interlocked.Exchange(ref _logicalDevicePublicationApplied, 1) != 0)
        {
            throw new InvalidOperationException(
                "The command runtime already consumed a logical-device publication.");
        }
        bool useCoreDynamicRendering = DeviceContext.InstanceApiVersion >= Silk.NET.Vulkan.Vk.Version13;
        bool useCoreSynchronization2 = DeviceContext.InstanceApiVersion >= Silk.NET.Vulkan.Vk.Version13;
        if (publication.UseCoreDynamicRenderingCommands != useCoreDynamicRendering ||
            publication.UseCoreSynchronization2Commands != useCoreSynchronization2 ||
            publication.DrawIndirectCountEnabled !=
                DeviceContext.MutableCapabilities._supportsDrawIndirectCount)
        {
            throw new InvalidOperationException(
                "The logical-device command publication disagrees with Device authority.");
        }
    }
}

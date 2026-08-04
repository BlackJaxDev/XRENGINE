namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal enum EVulkanLifetimeQueueDomain : byte
    {
        Graphics,
        Transfer,
        Other,
    }
}

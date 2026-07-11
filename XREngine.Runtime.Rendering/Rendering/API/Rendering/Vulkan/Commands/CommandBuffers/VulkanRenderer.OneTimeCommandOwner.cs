using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly struct OneTimeCommandOwner(CommandPool pool, bool useTransferQueue)
        {
            public CommandPool Pool { get; } = pool;
            public bool UseTransferQueue { get; } = useTransferQueue;
        }

    }
}

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly struct DeferredSecondaryCommandBuffer(CommandPool pool, CommandBuffer commandBuffer)
        {
            public CommandPool Pool { get; } = pool;
            public CommandBuffer CommandBuffer { get; } = commandBuffer;
        }

    }
}

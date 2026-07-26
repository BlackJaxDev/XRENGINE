using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private sealed class OwnedCommandChainSecondaryPool(CommandPool pool)
        {
            public CommandPool Pool { get; } = pool;
            public HashSet<ulong> CommandBuffers { get; } = [];
            public bool PendingDestroy { get; set; }
        }

    }
}

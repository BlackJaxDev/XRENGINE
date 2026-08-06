using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly struct OneTimeCommandOwner(CommandPool pool, bool useTransferQueue)
{
    public CommandPool Pool { get; } = pool;
    public bool UseTransferQueue { get; } = useTransferQueue;
}

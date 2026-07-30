using System.Collections.ObjectModel;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Immutable barrier generation published after a planner rebuild. Recording can
/// retain this object without observing a later mutation of the planner workspace.
/// </summary>
internal sealed class VulkanBarrierPlan
{
    public static VulkanBarrierPlan Empty { get; } = new(
        0,
        [],
        [],
        []);

    public VulkanBarrierPlan(
        ulong generation,
        VulkanBarrierPlanner.PlannedImageBarrier[] imageBarriers,
        VulkanBarrierPlanner.PlannedBufferBarrier[] bufferBarriers,
        VulkanBarrierPlanner.PlannedSwapchainBarrier[] swapchainBarriers)
    {
        Generation = generation;
        ImageBarriers = Array.AsReadOnly(imageBarriers);
        BufferBarriers = Array.AsReadOnly(bufferBarriers);
        SwapchainBarriers = Array.AsReadOnly(swapchainBarriers);
    }

    public ulong Generation { get; }
    public ReadOnlyCollection<VulkanBarrierPlanner.PlannedImageBarrier> ImageBarriers { get; }
    public ReadOnlyCollection<VulkanBarrierPlanner.PlannedBufferBarrier> BufferBarriers { get; }
    public ReadOnlyCollection<VulkanBarrierPlanner.PlannedSwapchainBarrier> SwapchainBarriers { get; }

    internal static VulkanBarrierPlan Capture(ulong generation, VulkanBarrierPlanner planner)
        => new(
            generation,
            [.. planner.ImageBarriers],
            [.. planner.BufferBarriers],
            [.. planner.SwapchainBarriers]);
}

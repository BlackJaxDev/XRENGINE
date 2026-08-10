using System.Collections.ObjectModel;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Immutable barrier generation published after a planner rebuild. Recording can
/// retain this object without observing a later mutation of the planner workspace.
/// </summary>
internal sealed class VulkanBarrierPlan
{
    private static readonly VulkanBarrierPlanner.PlannedImageBarrier[] EmptyImageBarriers = [];
    private static readonly VulkanBarrierPlanner.PlannedBufferBarrier[] EmptyBufferBarriers = [];
    private static readonly VulkanBarrierPlanner.PlannedSwapchainBarrier[] EmptySwapchainBarriers = [];
    private readonly Dictionary<int, VulkanBarrierPlanner.PlannedImageBarrier[]> _imageBarriersByPass;
    private readonly Dictionary<int, VulkanBarrierPlanner.PlannedBufferBarrier[]> _bufferBarriersByPass;
    private readonly Dictionary<int, VulkanBarrierPlanner.PlannedSwapchainBarrier[]> _swapchainBarriersByPass;

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
        _imageBarriersByPass = IndexByPass(imageBarriers, static barrier => barrier.PassIndex);
        _bufferBarriersByPass = IndexByPass(bufferBarriers, static barrier => barrier.PassIndex);
        _swapchainBarriersByPass = IndexByPass(swapchainBarriers, static barrier => barrier.PassIndex);
        HasCompleteNativeBindings = HasCompleteBindings(imageBarriers, bufferBarriers);
    }

    public ulong Generation { get; }
    public ReadOnlyCollection<VulkanBarrierPlanner.PlannedImageBarrier> ImageBarriers { get; }
    public ReadOnlyCollection<VulkanBarrierPlanner.PlannedBufferBarrier> BufferBarriers { get; }
    public ReadOnlyCollection<VulkanBarrierPlanner.PlannedSwapchainBarrier> SwapchainBarriers { get; }
    /// <summary>
    /// True when every physical barrier resource was resolved before the plan
    /// crossed into command recording.
    /// </summary>
    public bool HasCompleteNativeBindings { get; }

    internal IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier> GetImageBarriersForPass(int passIndex)
        => _imageBarriersByPass.TryGetValue(passIndex, out VulkanBarrierPlanner.PlannedImageBarrier[]? barriers)
            ? barriers
            : EmptyImageBarriers;

    internal IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier> GetBufferBarriersForPass(int passIndex)
        => _bufferBarriersByPass.TryGetValue(passIndex, out VulkanBarrierPlanner.PlannedBufferBarrier[]? barriers)
            ? barriers
            : EmptyBufferBarriers;

    internal IReadOnlyList<VulkanBarrierPlanner.PlannedSwapchainBarrier> GetSwapchainBarriersForPass(int passIndex)
        => _swapchainBarriersByPass.TryGetValue(passIndex, out VulkanBarrierPlanner.PlannedSwapchainBarrier[]? barriers)
            ? barriers
            : EmptySwapchainBarriers;

    private static Dictionary<int, T[]> IndexByPass<T>(T[] barriers, Func<T, int> resolvePassIndex)
    {
        Dictionary<int, List<T>> building = [];
        for (int index = 0; index < barriers.Length; index++)
        {
            T barrier = barriers[index];
            int passIndex = resolvePassIndex(barrier);
            if (!building.TryGetValue(passIndex, out List<T>? passBarriers))
            {
                passBarriers = [];
                building.Add(passIndex, passBarriers);
            }

            passBarriers.Add(barrier);
        }

        Dictionary<int, T[]> indexed = new(building.Count);
        foreach ((int passIndex, List<T> passBarriers) in building)
            indexed.Add(passIndex, [.. passBarriers]);
        return indexed;
    }

    internal static VulkanBarrierPlan Capture(ulong generation, VulkanBarrierPlanner planner)
        => new(
            generation,
            [.. planner.ImageBarriers],
            [.. planner.BufferBarriers],
            [.. planner.SwapchainBarriers]);

    private static bool HasCompleteBindings(
        VulkanBarrierPlanner.PlannedImageBarrier[] imageBarriers,
        VulkanBarrierPlanner.PlannedBufferBarrier[] bufferBarriers)
    {
        for (int index = 0; index < imageBarriers.Length; index++)
            if (imageBarriers[index].NativeImage.Handle == 0)
                return false;

        for (int index = 0; index < bufferBarriers.Length; index++)
            if (bufferBarriers[index].NativeBuffer.Handle == 0 ||
                bufferBarriers[index].NativeSize == 0)
            {
                return false;
            }

        return true;
    }
}

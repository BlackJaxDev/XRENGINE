namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>Typed, execution-facing identity for a resource frozen into a barrier plan.</summary>
internal readonly record struct VulkanResourceId(int Value)
{
    internal static VulkanResourceId Invalid => new(-1);
    internal bool IsValid => Value >= 0;
}

/// <summary>Contiguous slice of a frozen barrier array, indexed by pass identity.</summary>
internal readonly record struct VulkanBarrierPassRange(int PassIndex, int Offset, int Count);

/// <summary>Sorted numeric pass-index adjacency for one contiguous frozen barrier stream.</summary>
internal readonly record struct VulkanBarrierPassAdjacency(int[] PassIndices, VulkanBarrierPassRange[] Ranges)
{
    internal ReadOnlySpan<T> GetRange<T>(T[] barriers, int passIndex)
    {
        int low = 0;
        int high = PassIndices.Length - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int candidate = PassIndices[mid];
            if (candidate < passIndex)
            {
                low = mid + 1;
                continue;
            }
            if (candidate > passIndex)
            {
                high = mid - 1;
                continue;
            }

            VulkanBarrierPassRange range = Ranges[mid];
            return barriers.AsSpan(range.Offset, range.Count);
        }

        return ReadOnlySpan<T>.Empty;
    }
}

internal readonly record struct VulkanFrozenImageBarrier(
    int PassIndex, VulkanResourceId ResourceId, VulkanBarrierPlanner.ResolvedImageSubresourceRange Range,
    VulkanBarrierPlanner.PlannedImageState Previous, VulkanBarrierPlanner.PlannedImageState Next,
    uint SrcQueueFamilyIndex, uint DstQueueFamilyIndex, Silk.NET.Vulkan.Image NativeImage,
    Silk.NET.Vulkan.Format NativeFormat, bool IsBloomDiagnostic);
internal readonly record struct VulkanFrozenBufferBarrier(
    int PassIndex, VulkanResourceId ResourceId, VulkanBarrierPlanner.PlannedBufferState Previous,
    VulkanBarrierPlanner.PlannedBufferState Next, uint SrcQueueFamilyIndex, uint DstQueueFamilyIndex,
    Silk.NET.Vulkan.Buffer NativeBuffer, ulong NativeOffset, ulong NativeSize);
internal readonly record struct VulkanFrozenSwapchainBarrier(
    int PassIndex, VulkanResourceId ResourceId, XREngine.Rendering.RenderGraph.ERenderPassResourceType ResourceType,
    VulkanBarrierPlanner.PlannedImageState Previous, VulkanBarrierPlanner.PlannedImageState Next,
    uint SrcQueueFamilyIndex, uint DstQueueFamilyIndex);

/// <summary>
/// Immutable barrier generation published after planning. Authoring/planner dictionaries
/// remain cold; recording uses only contiguous arrays and pass-to-range adjacency.
/// </summary>
internal sealed class VulkanBarrierPlan
{
    private readonly VulkanBarrierPassAdjacency _imageRanges;
    private readonly VulkanBarrierPassAdjacency _bufferRanges;
    private readonly VulkanBarrierPassAdjacency _swapchainRanges;

    public static VulkanBarrierPlan Empty { get; } = new(0, [], [], []);

    public VulkanBarrierPlan(
        ulong generation,
        VulkanFrozenImageBarrier[] imageBarriers, VulkanFrozenBufferBarrier[] bufferBarriers,
        VulkanFrozenSwapchainBarrier[] swapchainBarriers)
    {
        Generation = generation;
        Array.Sort(imageBarriers, static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
        Array.Sort(bufferBarriers, static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
        Array.Sort(swapchainBarriers, static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
        _imageBarriers = imageBarriers;
        _bufferBarriers = bufferBarriers;
        _swapchainBarriers = swapchainBarriers;
        _imageRanges = BuildAdjacency(imageBarriers, static barrier => barrier.PassIndex);
        _bufferRanges = BuildAdjacency(bufferBarriers, static barrier => barrier.PassIndex);
        _swapchainRanges = BuildAdjacency(swapchainBarriers, static barrier => barrier.PassIndex);
        HasCompleteNativeBindings = HasCompleteBindings(_imageBarriers, _bufferBarriers);
    }

    public ulong Generation { get; }
    private readonly VulkanFrozenImageBarrier[] _imageBarriers;
    private readonly VulkanFrozenBufferBarrier[] _bufferBarriers;
    private readonly VulkanFrozenSwapchainBarrier[] _swapchainBarriers;
    /// <summary>Flat execution arrays exposed only as read-only spans.</summary>
    internal ReadOnlySpan<VulkanFrozenImageBarrier> ImageBarriers => _imageBarriers;
    internal ReadOnlySpan<VulkanFrozenBufferBarrier> BufferBarriers => _bufferBarriers;
    internal ReadOnlySpan<VulkanFrozenSwapchainBarrier> SwapchainBarriers => _swapchainBarriers;
    public bool HasCompleteNativeBindings { get; }

    internal ReadOnlySpan<VulkanFrozenImageBarrier> GetImageBarriersForPass(int passIndex)
        => _imageRanges.GetRange(_imageBarriers, passIndex);
    internal ReadOnlySpan<VulkanFrozenBufferBarrier> GetBufferBarriersForPass(int passIndex)
        => _bufferRanges.GetRange(_bufferBarriers, passIndex);
    internal ReadOnlySpan<VulkanFrozenSwapchainBarrier> GetSwapchainBarriersForPass(int passIndex)
        => _swapchainRanges.GetRange(_swapchainBarriers, passIndex);

    private static VulkanBarrierPassAdjacency BuildAdjacency<T>(T[] barriers, Func<T, int> passIndex)
    {
        if (barriers.Length == 0)
            return new VulkanBarrierPassAdjacency([], []);

        int rangeCount = 0;
        for (int index = 0; index < barriers.Length;)
        {
            int currentPass = passIndex(barriers[index]);
            rangeCount++;
            do
                index++;
            while (index < barriers.Length && passIndex(barriers[index]) == currentPass);
        }

        int[] passIndices = new int[rangeCount];
        VulkanBarrierPassRange[] ranges = new VulkanBarrierPassRange[rangeCount];
        int start = 0;
        int rangeIndex = 0;
        while (start < barriers.Length)
        {
            int pass = passIndex(barriers[start]);
            int end = start + 1;
            while (end < barriers.Length && passIndex(barriers[end]) == pass)
                end++;
            passIndices[rangeIndex] = pass;
            ranges[rangeIndex] = new VulkanBarrierPassRange(pass, start, end - start);
            rangeIndex++;
            start = end;
        }
        return new VulkanBarrierPassAdjacency(passIndices, ranges);
    }

    internal static VulkanBarrierPlan Capture(ulong generation, VulkanBarrierPlanner planner)
        => Capture(
            generation,
            planner.ImageBarriers,
            planner.BufferBarriers,
            planner.SwapchainBarriers,
            new VulkanRenderGraphResourceIds());

    internal static VulkanBarrierPlan Capture(
        ulong generation,
        IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier> plannerImages,
        IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier> plannerBuffers,
        IReadOnlyList<VulkanBarrierPlanner.PlannedSwapchainBarrier> plannerSwapchains,
        VulkanRenderGraphResourceIds resourceIds)
    {
        VulkanFrozenImageBarrier[] images = plannerImages.Select(barrier => new VulkanFrozenImageBarrier(barrier.PassIndex, resourceIds.GetOrAdd(barrier.ResourceName), barrier.Range, barrier.Previous, barrier.Next, barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex, barrier.NativeImage, barrier.NativeFormat, IsBloomName(barrier.ResourceName))).ToArray();
        VulkanFrozenBufferBarrier[] buffers = plannerBuffers.Select(barrier => new VulkanFrozenBufferBarrier(barrier.PassIndex, resourceIds.GetOrAdd(barrier.ResourceName), barrier.Previous, barrier.Next, barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex, barrier.NativeBuffer, barrier.NativeOffset, barrier.NativeSize)).ToArray();
        VulkanFrozenSwapchainBarrier[] swapchains = plannerSwapchains.Select(barrier => new VulkanFrozenSwapchainBarrier(barrier.PassIndex, resourceIds.GetOrAdd(barrier.ResourceName), barrier.ResourceType, barrier.Previous, barrier.Next, barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex)).ToArray();
        return new VulkanBarrierPlan(generation, images, buffers, swapchains);
    }

    private static bool IsBloomName(string name) => name.Contains("bloom", StringComparison.OrdinalIgnoreCase);

    private static bool HasCompleteBindings(
        VulkanFrozenImageBarrier[] imageBarriers, VulkanFrozenBufferBarrier[] bufferBarriers)
    {
        for (int index = 0; index < imageBarriers.Length; index++)
            if (imageBarriers[index].NativeImage.Handle == 0)
                return false;
        for (int index = 0; index < bufferBarriers.Length; index++)
            if (bufferBarriers[index].NativeBuffer.Handle == 0 || bufferBarriers[index].NativeSize == 0)
                return false;
        return true;
    }
}

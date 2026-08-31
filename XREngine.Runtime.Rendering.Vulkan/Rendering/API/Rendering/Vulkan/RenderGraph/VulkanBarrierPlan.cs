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
    int PassIndex, VulkanResourceId ResourceId, string LogicalResourceName, VulkanBarrierPlanner.PlannedBufferState Previous,
    VulkanBarrierPlanner.PlannedBufferState Next, uint SrcQueueFamilyIndex, uint DstQueueFamilyIndex,
    Silk.NET.Vulkan.Buffer NativeBuffer, ulong NativeOffset, ulong NativeSize, ulong NativeGeneration);
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

    public static VulkanBarrierPlan Empty { get; } = new(0, 0, [], [], []);

    public VulkanBarrierPlan(
        ulong generation,
        ulong nativeBufferBindingRevision,
        VulkanFrozenImageBarrier[] imageBarriers, VulkanFrozenBufferBarrier[] bufferBarriers,
        VulkanFrozenSwapchainBarrier[] swapchainBarriers)
    {
        Generation = generation;
        NativeBufferBindingRevision = nativeBufferBindingRevision;
        imageBarriers = CoalesceImageBarriers(imageBarriers);
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
    public ulong NativeBufferBindingRevision { get; }
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

    internal static VulkanBarrierPlan Capture(ulong generation, ulong nativeBufferBindingRevision, VulkanBarrierPlanner planner)
        => Capture(
            generation,
            nativeBufferBindingRevision,
            planner.ImageBarriers,
            planner.BufferBarriers,
            planner.SwapchainBarriers,
            new VulkanRenderGraphResourceIds());

    internal static VulkanBarrierPlan Capture(
        ulong generation,
        ulong nativeBufferBindingRevision,
        IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier> plannerImages,
        IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier> plannerBuffers,
        IReadOnlyList<VulkanBarrierPlanner.PlannedSwapchainBarrier> plannerSwapchains,
        VulkanRenderGraphResourceIds resourceIds)
    {
        VulkanFrozenImageBarrier[] images = plannerImages.Select(barrier => new VulkanFrozenImageBarrier(barrier.PassIndex, resourceIds.GetOrAdd(barrier.ResourceName), barrier.Range, barrier.Previous, barrier.Next, barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex, barrier.NativeImage, barrier.NativeFormat, IsBloomName(barrier.ResourceName))).ToArray();
        VulkanFrozenBufferBarrier[] buffers = plannerBuffers.Select(barrier => new VulkanFrozenBufferBarrier(barrier.PassIndex, resourceIds.GetOrAdd(barrier.ResourceName), barrier.ResourceName, barrier.Previous, barrier.Next, barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex, barrier.NativeBuffer, barrier.NativeOffset, barrier.NativeSize, barrier.NativeGeneration)).ToArray();
        VulkanFrozenSwapchainBarrier[] swapchains = plannerSwapchains.Select(barrier => new VulkanFrozenSwapchainBarrier(barrier.PassIndex, resourceIds.GetOrAdd(barrier.ResourceName), barrier.ResourceType, barrier.Previous, barrier.Next, barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex)).ToArray();
        return new VulkanBarrierPlan(generation, nativeBufferBindingRevision, images, buffers, swapchains);
    }

    private static bool IsBloomName(string name) => name.Contains("bloom", StringComparison.OrdinalIgnoreCase);

    private static VulkanFrozenImageBarrier[] CoalesceImageBarriers(
        VulkanFrozenImageBarrier[] barriers)
    {
        if (barriers.Length < 2)
            return barriers;

        Array.Sort(barriers, static (left, right) => CompareForCoalescing(left, right, layersFirst: true));
        VulkanFrozenImageBarrier[] layerMerged = MergeAdjacentRanges(barriers, mergeLayers: true);
        Array.Sort(layerMerged, static (left, right) => CompareForCoalescing(left, right, layersFirst: false));
        return MergeAdjacentRanges(layerMerged, mergeLayers: false);
    }

    private static VulkanFrozenImageBarrier[] MergeAdjacentRanges(
        VulkanFrozenImageBarrier[] barriers,
        bool mergeLayers)
    {
        VulkanFrozenImageBarrier[] merged = new VulkanFrozenImageBarrier[barriers.Length];
        int writeIndex = 0;
        for (int readIndex = 0; readIndex < barriers.Length; readIndex++)
        {
            VulkanFrozenImageBarrier candidate = barriers[readIndex];
            if (writeIndex > 0 &&
                TryMergeAdjacentRange(merged[writeIndex - 1], candidate, mergeLayers, out VulkanFrozenImageBarrier combined))
            {
                merged[writeIndex - 1] = combined;
                continue;
            }

            merged[writeIndex++] = candidate;
        }

        if (writeIndex != merged.Length)
            Array.Resize(ref merged, writeIndex);
        return merged;
    }

    private static bool TryMergeAdjacentRange(
        in VulkanFrozenImageBarrier left,
        in VulkanFrozenImageBarrier right,
        bool mergeLayers,
        out VulkanFrozenImageBarrier merged)
    {
        merged = default;
        if (!HasSameBarrierScope(left, right))
            return false;

        VulkanBarrierPlanner.ResolvedImageSubresourceRange leftRange = left.Range;
        VulkanBarrierPlanner.ResolvedImageSubresourceRange rightRange = right.Range;
        if (mergeLayers)
        {
            if (leftRange.BaseMipLevel != rightRange.BaseMipLevel ||
                leftRange.LevelCount != rightRange.LevelCount ||
                (ulong)leftRange.BaseArrayLayer + leftRange.LayerCount != rightRange.BaseArrayLayer)
            {
                return false;
            }

            merged = left with
            {
                Range = leftRange with
                {
                    LayerCount = checked(leftRange.LayerCount + rightRange.LayerCount),
                },
            };
            return true;
        }

        if (leftRange.BaseArrayLayer != rightRange.BaseArrayLayer ||
            leftRange.LayerCount != rightRange.LayerCount ||
            (ulong)leftRange.BaseMipLevel + leftRange.LevelCount != rightRange.BaseMipLevel)
        {
            return false;
        }

        merged = left with
        {
            Range = leftRange with
            {
                LevelCount = checked(leftRange.LevelCount + rightRange.LevelCount),
            },
        };
        return true;
    }

    private static bool HasSameBarrierScope(
        in VulkanFrozenImageBarrier left,
        in VulkanFrozenImageBarrier right)
        => left.PassIndex == right.PassIndex &&
           left.ResourceId == right.ResourceId &&
           left.Previous.Equals(right.Previous) &&
           left.Next.Equals(right.Next) &&
           left.SrcQueueFamilyIndex == right.SrcQueueFamilyIndex &&
           left.DstQueueFamilyIndex == right.DstQueueFamilyIndex &&
           left.NativeImage.Handle == right.NativeImage.Handle &&
           left.NativeFormat == right.NativeFormat &&
           left.IsBloomDiagnostic == right.IsBloomDiagnostic;

    private static int CompareForCoalescing(
        VulkanFrozenImageBarrier left,
        VulkanFrozenImageBarrier right,
        bool layersFirst)
    {
        int compare = left.PassIndex.CompareTo(right.PassIndex);
        if (compare != 0)
            return compare;
        compare = left.ResourceId.Value.CompareTo(right.ResourceId.Value);
        if (compare != 0)
            return compare;
        compare = left.NativeImage.Handle.CompareTo(right.NativeImage.Handle);
        if (compare != 0)
            return compare;
        compare = left.SrcQueueFamilyIndex.CompareTo(right.SrcQueueFamilyIndex);
        if (compare != 0)
            return compare;
        compare = left.DstQueueFamilyIndex.CompareTo(right.DstQueueFamilyIndex);
        if (compare != 0)
            return compare;
        compare = left.NativeFormat.CompareTo(right.NativeFormat);
        if (compare != 0)
            return compare;
        compare = left.IsBloomDiagnostic.CompareTo(right.IsBloomDiagnostic);
        if (compare != 0)
            return compare;
        compare = CompareImageState(left.Previous, right.Previous);
        if (compare != 0)
            return compare;
        compare = CompareImageState(left.Next, right.Next);
        if (compare != 0)
            return compare;

        VulkanBarrierPlanner.ResolvedImageSubresourceRange leftRange = left.Range;
        VulkanBarrierPlanner.ResolvedImageSubresourceRange rightRange = right.Range;
        if (layersFirst)
        {
            compare = leftRange.BaseMipLevel.CompareTo(rightRange.BaseMipLevel);
            if (compare != 0)
                return compare;
            compare = leftRange.LevelCount.CompareTo(rightRange.LevelCount);
            if (compare != 0)
                return compare;
            compare = leftRange.BaseArrayLayer.CompareTo(rightRange.BaseArrayLayer);
            return compare != 0
                ? compare
                : leftRange.LayerCount.CompareTo(rightRange.LayerCount);
        }

        compare = leftRange.BaseArrayLayer.CompareTo(rightRange.BaseArrayLayer);
        if (compare != 0)
            return compare;
        compare = leftRange.LayerCount.CompareTo(rightRange.LayerCount);
        if (compare != 0)
            return compare;
        compare = leftRange.BaseMipLevel.CompareTo(rightRange.BaseMipLevel);
        return compare != 0
            ? compare
            : leftRange.LevelCount.CompareTo(rightRange.LevelCount);
    }

    private static int CompareImageState(
        in VulkanBarrierPlanner.PlannedImageState left,
        in VulkanBarrierPlanner.PlannedImageState right)
    {
        int compare = left.Layout.CompareTo(right.Layout);
        if (compare != 0)
            return compare;
        compare = left.StageMask.CompareTo(right.StageMask);
        if (compare != 0)
            return compare;
        compare = left.AccessMask.CompareTo(right.AccessMask);
        return compare != 0
            ? compare
            : left.AspectMask.CompareTo(right.AspectMask);
    }

    private static bool HasCompleteBindings(
        VulkanFrozenImageBarrier[] imageBarriers, VulkanFrozenBufferBarrier[] bufferBarriers)
    {
        for (int index = 0; index < imageBarriers.Length; index++)
            if (imageBarriers[index].NativeImage.Handle == 0)
                return false;
        for (int index = 0; index < bufferBarriers.Length; index++)
            if (bufferBarriers[index].NativeBuffer.Handle == 0 || bufferBarriers[index].NativeSize == 0 || bufferBarriers[index].NativeGeneration == 0)
                return false;
        return true;
    }
}

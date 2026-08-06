using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Owns deterministic frame-operation ordering and secondary-recording bucket construction.
/// </summary>
internal sealed class VulkanFrameOperationScheduler
{
    private const string RenderUiBatchedPassNamePrefix = "RenderUIBatched_";
    private const int MaxMetadataCacheEntries = 64;
    private readonly struct FrameOpSortKey(
        FrameOp operation,
        int contextBlockOrder,
        int passOrder,
        int originalIndex,
        int queryOrderBlock)
    {
        public FrameOp Operation { get; } = operation;
        public int ContextBlockOrder { get; } = contextBlockOrder;
        public int PassOrder { get; } = passOrder;
        public int OriginalIndex { get; } = originalIndex;
        public int QueryOrderBlock { get; } = queryOrderBlock;
    }

    private readonly struct SchedulingTargetKey(
        int passOrder,
        int schedulingIdentity,
        object? target) : IEquatable<SchedulingTargetKey>
    {
        private readonly int _passOrder = passOrder;
        private readonly int _schedulingIdentity = schedulingIdentity;
        private readonly object? _target = target;

        public bool Equals(SchedulingTargetKey other)
            => _passOrder == other._passOrder &&
               _schedulingIdentity == other._schedulingIdentity &&
               ReferenceEquals(_target, other._target);

        public override bool Equals(object? obj)
            => obj is SchedulingTargetKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(
                _passOrder,
                _schedulingIdentity,
                _target is null ? 0 : RuntimeHelpers.GetHashCode(_target));
    }

    private sealed class FrameOpSortKeyComparer : IComparer<FrameOpSortKey>
    {
        public static readonly FrameOpSortKeyComparer Instance = new();

        public int Compare(FrameOpSortKey x, FrameOpSortKey y)
        {
            int blockCompare = x.ContextBlockOrder.CompareTo(y.ContextBlockOrder);
            if (blockCompare != 0)
                return blockCompare;

            int passCompare = x.PassOrder.CompareTo(y.PassOrder);
            if (passCompare != 0)
                return passCompare;

            // Passes absent from graph metadata share the same fallback rank. Use
            // one global query-boundary ordinal at that rank so draws from another
            // equal-ranked pass cannot make this comparator non-transitive or enter
            // an inline query bracket.
            int queryBlockCompare = x.QueryOrderBlock.CompareTo(y.QueryOrderBlock);
            if (queryBlockCompare != 0)
                return queryBlockCompare;

            if (x.Operation is MeshDrawOp xDraw &&
                y.Operation is MeshDrawOp yDraw &&
                CanCanonicalizeMeshDrawOrder(xDraw) &&
                CanCanonicalizeMeshDrawOrder(yDraw))
            {
                int drawCompare = CompareCanonicalMeshDrawOrder(xDraw, yDraw);
                if (drawCompare != 0)
                    return drawCompare;
            }

            return x.OriginalIndex.CompareTo(y.OriginalIndex);
        }

        private static bool CanCanonicalizeMeshDrawOrder(MeshDrawOp op)
            => op.Draw.Renderer is not null &&
               !op.Draw.BlendEnabled &&
               !op.PreserveSubmissionOrder &&
               !IsUiPipelineDraw(op);

        private static bool IsUiPipelineDraw(MeshDrawOp op)
            => op.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline;

        private static int CompareCanonicalMeshDrawOrder(MeshDrawOp x, MeshDrawOp y)
        {
            // Material sorting must stay inside one render-view cohort. Sorting the
            // same mesh from several directional cascades together interleaves their
            // cameras and viewport/scissor state, which breaks contiguous secondary
            // batches and forces a begin/barrier/draw/end sequence per mesh.
            int viewCompare = CompareRenderViewCohort(x.Context, y.Context);
            if (viewCompare != 0)
                return viewCompare;

            int targetCompare = (x.Target?.GetHashCode() ?? 0).CompareTo(y.Target?.GetHashCode() ?? 0);
            if (targetCompare != 0)
                return targetCompare;

            if (x.Draw.ShadowUniformState.IsShadowPass &&
                y.Draw.ShadowUniformState.IsShadowPass)
            {
                int shadowBucketCompare =
                    VulkanRenderer.ResolveShadowCommandChainBucket(x)
                        .CompareTo(
                            VulkanRenderer.ResolveShadowCommandChainBucket(y));
                if (shadowBucketCompare != 0)
                    return shadowBucketCompare;
            }

            int materialCompare = (x.Draw.MaterialOverride?.GetHashCode() ?? 0).CompareTo(y.Draw.MaterialOverride?.GetHashCode() ?? 0);
            if (materialCompare != 0)
                return materialCompare;

            int rendererCompare = x.Draw.Renderer.GetHashCode().CompareTo(y.Draw.Renderer.GetHashCode());
            if (rendererCompare != 0)
                return rendererCompare;

            int instanceCompare = x.Draw.Instances.CompareTo(y.Draw.Instances);
            if (instanceCompare != 0)
                return instanceCompare;

            return ((int)x.Draw.BillboardMode).CompareTo((int)y.Draw.BillboardMode);
        }

        private static int CompareRenderViewCohort(in FrameOpContext x, in FrameOpContext y)
        {
            // SchedulingIdentity is the exact primary-recording/render-scope
            // boundary. Sequential directional cascades intentionally share the
            // pipeline, viewport, output atlas, and pass metadata, so comparing
            // only those broader identities still interleaves cascade draws by
            // material. Keep every scope contiguous before applying mesh order.
            int compare = x.SchedulingIdentity.CompareTo(y.SchedulingIdentity);
            if (compare != 0)
                return compare;

            compare = x.PipelineIdentity.CompareTo(y.PipelineIdentity);
            if (compare != 0)
                return compare;

            compare = x.ViewportIdentity.CompareTo(y.ViewportIdentity);
            if (compare != 0)
                return compare;

            compare = x.OutputTargetIdentity.CompareTo(y.OutputTargetIdentity);
            if (compare != 0)
                return compare;

            compare = x.ContextKind.CompareTo(y.ContextKind);
            if (compare != 0)
                return compare;

            compare = x.StereoEnabled.CompareTo(y.StereoEnabled);
            return compare != 0
                ? compare
                : x.MultiviewEnabled.CompareTo(y.MultiviewEnabled);
        }
    }
    private static readonly Comparison<FrameOpSortKey> FrameOpSortComparison =
        FrameOpSortKeyComparer.Instance.Compare;

    private sealed class PassOrderCacheEntry
    {
        public PassOrderCacheEntry(IReadOnlyCollection<RenderPassMetadata> metadata)
        {
            IReadOnlyList<RenderPassMetadata> orderedPasses = RenderGraphSynchronizationPlanner.TopologicallySort(metadata);
            Dictionary<int, int> passOrder = new(orderedPasses.Count);
            for (int i = 0; i < orderedPasses.Count; i++)
                passOrder[orderedPasses[i].PassIndex] = i;

            PassOrder = passOrder;
        }

        public IReadOnlyDictionary<int, int> PassOrder { get; }
    }

    private readonly ConcurrentDictionary<IReadOnlyCollection<RenderPassMetadata>, PassOrderCacheEntry>
        _passOrderCache = new(ReferenceEqualityComparer.Instance);
    private FrameOpSortKey[] _sortKeyScratch = new FrameOpSortKey[256];
    private FrameOpSortKey[] _clearReorderScratch = new FrameOpSortKey[256];
    private int[] _nextClearIndexScratch = new int[256];
    private readonly Dictionary<int, int> _contextBlockOrderScratch = new();
    private readonly Dictionary<SchedulingTargetKey, int> _earliestTargetUseScratch = new();
    private readonly Dictionary<SchedulingTargetKey, int> _firstTargetClearScratch = new();
    private readonly Dictionary<SchedulingTargetKey, int> _lastTargetClearScratch = new();
    internal void ReleaseCaches()
        => _passOrderCache.Clear();

    private void TrimMetadataCachesIfRequired()
    {
        if (_passOrderCache.Count < MaxMetadataCacheEntries)
            return;

        _passOrderCache.Clear();
    }

    /// <summary>
    /// Sorts frame operations deterministically by:
    /// 1) compiled pass topological order,
    /// 2) render-view cohort, then canonical opaque mesh draw order when both operations are safe to reorder,
    /// 3) original index for all dependency-carrying operations,
    /// 4) same-pass target clear-before-use normalization.
    /// </summary>
    /// <remarks>
    /// Pass order must dominate scheduling groups so consumers cannot be recorded before
    /// producers when different pipeline/viewport contexts enqueue related work. The pass
    /// rank is resolved from the compiled frame graph first; per-context metadata is only
    /// a fallback for nested work that is absent from the active graph.
    /// Same-pass operations preserve original enqueue order unless both are canonicalizable
    /// opaque mesh draws. After sorting, target clears are lifted just far enough to precede
    /// earlier uses of the same scheduling context and exact target; this keeps clears from
    /// landing after desktop/HMD work when simultaneous render contexts interleave.
    /// </remarks>
    /// <param name="ops">Operations to sort.</param>
    /// <param name="graph">Compiled pass-order metadata.</param>
    /// <returns>The input array, sorted in place (or unchanged for length 0/1).</returns>
    public static FrameOp[] SortFrameOps(FrameOp[] ops, VulkanCompiledRenderGraph graph)
        => new VulkanFrameOperationScheduler().SortFrameOpsCore(ops, graph);

    /// <summary>
    /// Sorts frame operations using caches owned by the active renderer generation.
    /// </summary>
    public FrameOp[] SortFrameOpsCore(FrameOp[] ops, VulkanCompiledRenderGraph graph)
    {
        // Fast path: trivial arrays are already sorted and preserving reference identity helps tests.
        if (ops.Length <= 1)
            return ops;

        int opCount = ops.Length;
        EnsureSortScratchCapacity(opCount);
        FrameOpSortKey[] sortKeys = _sortKeyScratch;

        try
        {
            bool preserveContextBlocks = HasSubmissionOrderBlock(ops);
            if (preserveContextBlocks)
                BuildContextBlockOrders(ops);
            int queryOrderBlock = 0;

            for (int i = 0; i < opCount; i++)
            {
                FrameOp op = ops[i];
                sortKeys[i] = new FrameOpSortKey(
                    op,
                    preserveContextBlocks
                        ? _contextBlockOrderScratch[op.Context.SchedulingIdentity]
                        : 0,
                    ResolvePassOrder(op, graph),
                    i,
                    queryOrderBlock);

                // The current query op terminates its preceding order block. A
                // single forward ordinal makes this O(N) and fences equal-ranked
                // passes as well as operations with the same PassIndex.
                if (op is QueryOp)
                    queryOrderBlock++;
            }

            bool alreadySorted = true;
            for (int i = 1; i < opCount; i++)
            {
                if (FrameOpSortKeyComparer.Instance.Compare(sortKeys[i - 1], sortKeys[i]) <= 0)
                    continue;

                alreadySorted = false;
                break;
            }

            if (!alreadySorted)
                SortFrameOpKeysInPlace(sortKeys, opCount);

            bool movedTargetClear = MoveTargetClearsBeforeFirstSameTargetUse(sortKeys, opCount);
            if (alreadySorted && !movedTargetClear)
                return ops;

            for (int i = 0; i < opCount; i++)
                ops[i] = sortKeys[i].Operation;

            return ops;
        }
        finally
        {
            Array.Clear(sortKeys, 0, opCount);
            Array.Clear(_clearReorderScratch, 0, opCount);
            _contextBlockOrderScratch.Clear();
            _earliestTargetUseScratch.Clear();
            _firstTargetClearScratch.Clear();
            _lastTargetClearScratch.Clear();
        }
    }

    private void EnsureSortScratchCapacity(int required)
    {
        if (_sortKeyScratch.Length >= required)
            return;

        int capacity = Math.Max(required, _sortKeyScratch.Length * 2);
        Array.Resize(ref _sortKeyScratch, capacity);
        Array.Resize(ref _clearReorderScratch, capacity);
        Array.Resize(ref _nextClearIndexScratch, capacity);
    }

    /// <summary>
    /// Sorts the warmed frame-op scratch with the span introsort.
    /// Camera motion can interleave hundreds of cascade and scene operations, so an
    /// insertion sort here becomes quadratic precisely when frame time matters most.
    /// The comparison delegate is cached because adapting an <see cref="IComparer{T}"/>
    /// inside the generic sort helper allocates once per non-trivial command re-record.
    /// </summary>
    private static void SortFrameOpKeysInPlace(FrameOpSortKey[] sortKeys, int opCount)
        => sortKeys.AsSpan(0, opCount).Sort(FrameOpSortComparison);

    private static bool HasSubmissionOrderBlock(FrameOp[] ops)
    {
        for (int i = 0; i < ops.Length; i++)
        {
            if (ops[i].Context.PreserveSubmissionOrderBlock)
                return true;
        }

        return false;
    }

    private void BuildContextBlockOrders(FrameOp[] ops)
    {
        _contextBlockOrderScratch.Clear();
        for (int index = 0; index < ops.Length; index++)
            _contextBlockOrderScratch.TryAdd(
                ops[index].Context.SchedulingIdentity,
                index);
    }

    private bool MoveTargetClearsBeforeFirstSameTargetUse(FrameOpSortKey[] sortKeys, int opCount)
    {
        _earliestTargetUseScratch.Clear();
        _firstTargetClearScratch.Clear();
        _lastTargetClearScratch.Clear();

        for (int index = 0; index < opCount; index++)
        {
            FrameOpSortKey sortKey = sortKeys[index];
            FrameOp operation = sortKey.Operation;
            SchedulingTargetKey targetKey = CreateSchedulingTargetKey(sortKey);
            if (IsTargetUseThatClearMustPrecede(operation))
            {
                _earliestTargetUseScratch.TryAdd(targetKey, index);
                continue;
            }

            if (operation is not ClearOp)
                continue;

            _nextClearIndexScratch[index] = -1;
            if (_lastTargetClearScratch.TryGetValue(targetKey, out int previousClearIndex))
                _nextClearIndexScratch[previousClearIndex] = index;
            else
                _firstTargetClearScratch.Add(targetKey, index);
            _lastTargetClearScratch[targetKey] = index;
        }

        bool moved = false;
        int writeIndex = 0;
        for (int index = 0; index < opCount; index++)
        {
            FrameOpSortKey sortKey = sortKeys[index];
            SchedulingTargetKey targetKey = CreateSchedulingTargetKey(sortKey);
            if (sortKey.Operation is ClearOp &&
                _earliestTargetUseScratch.TryGetValue(targetKey, out int earliestUseIndex) &&
                index > earliestUseIndex)
            {
                moved = true;
                continue;
            }

            if (IsTargetUseThatClearMustPrecede(sortKey.Operation) &&
                _earliestTargetUseScratch[targetKey] == index &&
                _firstTargetClearScratch.TryGetValue(targetKey, out int clearIndex))
            {
                while (clearIndex >= 0)
                {
                    if (clearIndex > index)
                        _clearReorderScratch[writeIndex++] = sortKeys[clearIndex];
                    clearIndex = _nextClearIndexScratch[clearIndex];
                }
            }

            _clearReorderScratch[writeIndex++] = sortKey;
        }

        if (moved)
            Array.Copy(_clearReorderScratch, sortKeys, opCount);

        return moved;
    }

    private static SchedulingTargetKey CreateSchedulingTargetKey(in FrameOpSortKey sortKey)
        => new(
            sortKey.PassOrder,
            sortKey.Operation.Context.SchedulingIdentity,
            sortKey.Operation.Target);

    private static bool IsTargetUseThatClearMustPrecede(FrameOp op)
        => op is MeshDrawOp or QueryOp or BlitOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp or TransformFeedbackOp;

    private int ResolvePassOrder(FrameOp op, VulkanCompiledRenderGraph graph)
    {
        if (op is TextureUploadFrameOp)
            return int.MinValue;

        if (TryResolveNestedScreenSpaceUiPassOrder(op, graph, out int screenSpaceUiOrder))
            return screenSpaceUiOrder;

        if (graph.PassOrder.TryGetValue(op.PassIndex, out int graphOrder))
            return graphOrder;

        if (op.Context.PassMetadata is { Count: > 0 } metadata)
        {
            TrimMetadataCachesIfRequired();
            IReadOnlyDictionary<int, int> contextPassOrder = _passOrderCache.GetOrAdd(
                metadata,
                static key => new PassOrderCacheEntry(key)).PassOrder;

            if (contextPassOrder.TryGetValue(op.PassIndex, out int contextOrder))
                return contextOrder;
        }

        return int.MaxValue;
    }

    private static bool TryResolveNestedScreenSpaceUiPassOrder(
        FrameOp op,
        VulkanCompiledRenderGraph graph,
        out int passOrder)
    {
        passOrder = 0;

        if (!VulkanSwapchainContextCoalescer.TargetsSwapchain(op) || !IsNestedUiPipelineOp(op))
            return false;

        if (graph.ScreenSpaceUiPassOrder == int.MaxValue)
            return false;

        passOrder = graph.ScreenSpaceUiPassOrder;
        return true;
    }

    private static bool IsNestedUiPipelineOp(FrameOp op)
    {
        if (op.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
            return true;

        if (op.Context.PassMetadata is not { } metadata)
            return false;

        if (metadata is IReadOnlyList<RenderPassMetadata> list)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Name.StartsWith(RenderUiBatchedPassNamePrefix, StringComparison.Ordinal))
                    return true;

            return false;
        }

        // Pipeline pass metadata is normally array/list-backed. Preserve support for
        // custom collection implementations outside the frame-wide desktop fast path.
        foreach (RenderPassMetadata pass in metadata)
            if (pass.Name.StartsWith(RenderUiBatchedPassNamePrefix, StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>
    /// Builds contiguous runs of secondary-command-buffer-eligible operations.
    /// A run ends when op type, pass, scheduling identity, or full context changes.
    /// </summary>
    /// <param name="ops">Sorted frame operations for the current frame.</param>
    /// <param name="destination">Caller-owned reusable destination; cleared before use.</param>
    public void BuildSecondaryRecordingBuckets(
        FrameOperationSequence ops,
        List<VulkanSecondaryRecordingBucket> destination)
    {
        destination.Clear();
        if (ops.Length == 0)
            return;

        List<VulkanSecondaryRecordingBucket> buckets = destination;
        int runStart = -1;
        int runPassIndex = int.MinValue;
        int runTargetIdentity = int.MinValue;
        int runSchedulingIdentity = int.MinValue;
        EVulkanSecondaryCommandFamily runFamily = default;
        Type? runType = null;
        FrameOpContext runContext = default;

        for (int i = 0; i < ops.Length; i++)
        {
            FrameOp op = ops[i];
            if (!TryResolveSecondaryCommandFamily(op, out EVulkanSecondaryCommandFamily family))
            {
                // Ineligible ops break the current run.
                FinalizeRun(i);
                continue;
            }

            int passIndex = op.PassIndex;
            int targetIdentity = ResolveFrameOpTargetIdentity(op);
            int schedulingIdentity = op.Context.SchedulingIdentity;
            Type opType = op.GetType();

            if (runStart < 0)
            {
                runStart = i;
                runPassIndex = passIndex;
                runTargetIdentity = targetIdentity;
                runSchedulingIdentity = schedulingIdentity;
                runFamily = family;
                runType = opType;
                runContext = op.Context;
                continue;
            }

            // Runs must remain homogeneous to be safely co-recorded.
            bool sameBucket =
                runType == opType &&
                runPassIndex == passIndex &&
                runTargetIdentity == targetIdentity &&
                runSchedulingIdentity == schedulingIdentity &&
                runFamily == family &&
                FrameOpContextCompatibility.AreRecordingCompatible(runContext, op.Context);

            if (!sameBucket)
            {
                // Close previous run and start a new compatible run at i.
                FinalizeRun(i);
                runStart = i;
                runPassIndex = passIndex;
                runTargetIdentity = targetIdentity;
                runSchedulingIdentity = schedulingIdentity;
                runFamily = family;
                runType = opType;
                runContext = op.Context;
            }
        }

        FinalizeRun(ops.Length);
        void FinalizeRun(int runEndExclusive)
        {
            if (runStart < 0 || runType is null)
                return;

            int runCount = runEndExclusive - runStart;
            if (runCount > 0)
            {
                // Emit one bucket per contiguous compatible run.
                buckets.Add(new VulkanSecondaryRecordingBucket(
                    runStart,
                    runCount,
                    runPassIndex,
                    runTargetIdentity,
                    runSchedulingIdentity,
                    runFamily,
                    runType,
                    runContext));
            }

            runStart = -1;
            runPassIndex = int.MinValue;
            runTargetIdentity = int.MinValue;
            runSchedulingIdentity = int.MinValue;
            runFamily = default;
            runType = null;
            runContext = default;
        }
    }

    /// <summary>
    /// Determines whether an op type participates in secondary command recording buckets.
    /// </summary>
    private static bool TryResolveSecondaryCommandFamily(
        FrameOp op,
        out EVulkanSecondaryCommandFamily family)
    {
        switch (op)
        {
            case ComputeDispatchOp:
            case ComputeDispatchIndirectOp:
                family = EVulkanSecondaryCommandFamily.Compute;
                return true;
            case MemoryBarrierOp:
                family = EVulkanSecondaryCommandFamily.Synchronization;
                return true;
            case BufferCopyOp:
                family = EVulkanSecondaryCommandFamily.Transfer;
                return true;
            case QueryOp:
                family = EVulkanSecondaryCommandFamily.Query;
                return true;
            default:
                family = default;
                return false;
        }
    }

    private static int ResolveFrameOpTargetIdentity(FrameOp op)
        => op.Target?.GetHashCode() ?? 0;

}

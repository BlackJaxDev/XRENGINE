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
        int operationIndex,
        int contextBlockOrder,
        int passOrder,
        int originalIndex,
        int queryOrderBlock,
        VulkanMeshDrawSortKey meshDrawKey,
        EVulkanPrimaryPlanNodeKind opCode,
        int schedulingIdentity,
        XRFrameBuffer? target)
    {
        public int OperationIndex { get; } = operationIndex;
        public int ContextBlockOrder { get; } = contextBlockOrder;
        public int PassOrder { get; } = passOrder;
        public int OriginalIndex { get; } = originalIndex;
        public int QueryOrderBlock { get; } = queryOrderBlock;
        public VulkanMeshDrawSortKey MeshDrawKey { get; } = meshDrawKey;
        public EVulkanPrimaryPlanNodeKind OpCode { get; } = opCode;
        public int SchedulingIdentity { get; } = schedulingIdentity;
        public XRFrameBuffer? Target { get; } = target;
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

            VulkanMeshDrawSortKey xKey = x.MeshDrawKey;
            VulkanMeshDrawSortKey yKey = y.MeshDrawKey;
            if (xKey.CanCanonicalize && yKey.CanCanonicalize)
            {
                int drawCompare = CompareCanonicalMeshDrawOrder(in xKey, in yKey);
                if (drawCompare != 0)
                    return drawCompare;
            }

            return x.OriginalIndex.CompareTo(y.OriginalIndex);
        }

        private static int CompareCanonicalMeshDrawOrder(
            in VulkanMeshDrawSortKey x,
            in VulkanMeshDrawSortKey y)
        {
            // Material sorting must stay inside one render-view cohort. Sorting the
            // same mesh from several directional cascades together interleaves their
            // cameras and viewport/scissor state, which breaks contiguous secondary
            // batches and forces a begin/barrier/draw/end sequence per mesh.
            int viewCompare = CompareRenderViewCohort(in x, in y);
            if (viewCompare != 0)
                return viewCompare;

            int targetCompare = x.TargetIdentity.CompareTo(y.TargetIdentity);
            if (targetCompare != 0)
                return targetCompare;

            if (x.ShadowPass && y.ShadowPass)
            {
                int shadowBucketCompare = x.ShadowBucket.CompareTo(y.ShadowBucket);
                if (shadowBucketCompare != 0)
                    return shadowBucketCompare;
            }

            int materialCompare = x.MaterialIdentity.CompareTo(y.MaterialIdentity);
            if (materialCompare != 0)
                return materialCompare;

            int rendererCompare = x.RendererIdentity.CompareTo(y.RendererIdentity);
            if (rendererCompare != 0)
                return rendererCompare;

            int instanceCompare = x.InstanceCount.CompareTo(y.InstanceCount);
            if (instanceCompare != 0)
                return instanceCompare;

            return x.BillboardMode.CompareTo(y.BillboardMode);
        }

        private static int CompareRenderViewCohort(
            in VulkanMeshDrawSortKey x,
            in VulkanMeshDrawSortKey y)
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
    private int[] _operationOrderScratch = new int[256];
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
    /// Sorts lowered frame-operation headers deterministically by:
    /// 1) the operation pipeline's topological pass order, with the compiled graph as fallback,
    /// 2) render-view cohort, then canonical opaque mesh draw order when both operations are safe to reorder,
    /// 3) original index for all dependency-carrying operations,
    /// 4) same-pass target clear-before-use normalization.
    /// </summary>
    /// <remarks>
    /// Pass order must dominate scheduling groups so consumers cannot be recorded before
    /// producers when different pipeline/viewport contexts enqueue related work. Each
    /// operation's pipeline metadata is authoritative because the published resource graph
    /// can belong to another context; its compiled rank is used only when that metadata does
    /// not describe the operation.
    /// Same-pass operations preserve original enqueue order unless both are canonicalizable
    /// opaque mesh draws. After sorting, target clears are lifted just far enough to precede
    /// earlier uses of the same scheduling context and exact target; this keeps clears from
    /// landing after desktop/HMD work when simultaneous render contexts interleave.
    /// </remarks>
    /// <param name="operations">Lowered operation stream to reorder.</param>
    /// <param name="graph">Compiled pass-order metadata.</param>
    internal void SortLoweredOperations(
        FrameOperationStream operations,
        VulkanCompiledRenderGraph graph)
    {
        if (operations.Count <= 1)
            return;

        int opCount = operations.Count;
        EnsureSortScratchCapacity(opCount);
        FrameOpSortKey[] sortKeys = _sortKeyScratch;

        try
        {
            bool preserveContextBlocks = HasSubmissionOrderBlock(operations);
            if (preserveContextBlocks)
                BuildContextBlockOrders(operations);
            int queryOrderBlock = 0;
            IReadOnlyCollection<RenderPassMetadata>? cachedContextMetadata = null;
            IReadOnlyDictionary<int, int>? cachedContextPassOrder = null;

            for (int i = 0; i < opCount; i++)
            {
                ref readonly FrameOperationHeader header = ref operations.GetHeader(i);
                ref readonly FrameOpContext context = ref operations.GetContext(i);
                XRFrameBuffer? target = operations.GetTarget(i);
                VulkanMeshDrawSortKey meshDrawKey = default;
                if (header.OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw)
                {
                    MeshDrawPayload payload = operations.GetMeshDraw(i);
                    meshDrawKey = VulkanMeshDrawSortKey.Capture(
                        payload.Draw,
                        in context,
                        target,
                        header.PreserveSubmissionOrder);
                }
                sortKeys[i] = new FrameOpSortKey(
                    i,
                    preserveContextBlocks
                        ? _contextBlockOrderScratch[context.SchedulingIdentity]
                        : 0,
                    ResolvePassOrder(
                        in header,
                        in context,
                        target,
                        graph,
                        ref cachedContextMetadata,
                        ref cachedContextPassOrder),
                    header.OriginalIndex,
                    queryOrderBlock,
                    meshDrawKey,
                    header.OpCode,
                    context.SchedulingIdentity,
                    target);

                // The current query op terminates its preceding order block. A
                // single forward ordinal makes this O(N) and fences equal-ranked
                // passes as well as operations with the same PassIndex.
                if (header.OpCode == EVulkanPrimaryPlanNodeKind.Query)
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
                return;

            for (int i = 0; i < opCount; i++)
                _operationOrderScratch[i] = sortKeys[i].OperationIndex;
            operations.Reorder(_operationOrderScratch.AsSpan(0, opCount));
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
        Array.Resize(ref _operationOrderScratch, capacity);
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

    private static bool HasSubmissionOrderBlock(FrameOperationStream operations)
    {
        for (int i = 0; i < operations.Count; i++)
        {
            if (operations.GetContext(i).PreserveSubmissionOrderBlock)
                return true;
        }

        return false;
    }

    private void BuildContextBlockOrders(FrameOperationStream operations)
    {
        _contextBlockOrderScratch.Clear();
        for (int index = 0; index < operations.Count; index++)
            _contextBlockOrderScratch.TryAdd(
                operations.GetContext(index).SchedulingIdentity,
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
            SchedulingTargetKey targetKey = CreateSchedulingTargetKey(sortKey);
            if (IsTargetUseThatClearMustPrecede(sortKey))
            {
                _earliestTargetUseScratch.TryAdd(targetKey, index);
                continue;
            }

            if (!IsClear(sortKey))
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
            if (IsClear(sortKey) &&
                _earliestTargetUseScratch.TryGetValue(targetKey, out int earliestUseIndex) &&
                index > earliestUseIndex)
            {
                moved = true;
                continue;
            }

            if (IsTargetUseThatClearMustPrecede(sortKey) &&
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
            sortKey.SchedulingIdentity,
            sortKey.Target);

    private static bool IsClear(in FrameOpSortKey sortKey)
        => sortKey.OpCode == EVulkanPrimaryPlanNodeKind.Clear;

    private static bool IsTargetUseThatClearMustPrecede(in FrameOpSortKey sortKey)
        => sortKey.OpCode is EVulkanPrimaryPlanNodeKind.MeshDraw or
            EVulkanPrimaryPlanNodeKind.Query or
            EVulkanPrimaryPlanNodeKind.Blit or
            EVulkanPrimaryPlanNodeKind.IndirectDraw or
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount or
            EVulkanPrimaryPlanNodeKind.TransformFeedback;

    private int ResolvePassOrder(
        in FrameOperationHeader header,
        in FrameOpContext context,
        XRFrameBuffer? target,
        VulkanCompiledRenderGraph graph,
        ref IReadOnlyCollection<RenderPassMetadata>? cachedContextMetadata,
        ref IReadOnlyDictionary<int, int>? cachedContextPassOrder)
    {
        if (header.OpCode == EVulkanPrimaryPlanNodeKind.TextureUpload)
            return int.MinValue;

        if (TryResolveNestedScreenSpaceUiPassOrder(in header, in context, target, graph, out int screenSpaceUiOrder))
            return screenSpaceUiOrder;

        if (context.PassMetadata is { Count: > 0 } metadata)
        {
            if (!ReferenceEquals(metadata, cachedContextMetadata))
            {
                TrimMetadataCachesIfRequired();
                cachedContextMetadata = metadata;
                cachedContextPassOrder = _passOrderCache.GetOrAdd(
                    metadata,
                    static key => new PassOrderCacheEntry(key)).PassOrder;
            }

            if (cachedContextPassOrder is not null &&
                cachedContextPassOrder.TryGetValue(header.PassIndex, out int contextOrder))
            {
                return contextOrder;
            }
        }

        // A published graph can belong to another planner context (for example a
        // directional-shadow update). Never mix ranks from that partial graph with
        // ranks from this operation's complete pipeline metadata: doing so moved
        // Background ahead of the ForwardPass clear that it explicitly depends on.
        if (graph.Plan.Execution.TryGetPassOrder(header.PassIndex, out int graphOrder))
            return graphOrder;

        return int.MaxValue;
    }

    private static bool TryResolveNestedScreenSpaceUiPassOrder(
        in FrameOperationHeader header,
        in FrameOpContext context,
        XRFrameBuffer? target,
        VulkanCompiledRenderGraph graph,
        out int passOrder)
    {
        passOrder = 0;

        if (!TargetsSwapchain(in header, target) || !IsNestedUiPipelineOp(in context))
            return false;

        if (graph.ScreenSpaceUiPassOrder == int.MaxValue)
            return false;

        passOrder = graph.ScreenSpaceUiPassOrder;
        return true;
    }

    private static bool TargetsSwapchain(in FrameOperationHeader header, XRFrameBuffer? target)
        => target is null && header.OpCode is
            EVulkanPrimaryPlanNodeKind.Clear or
            EVulkanPrimaryPlanNodeKind.MeshDraw or
            EVulkanPrimaryPlanNodeKind.Query or
            EVulkanPrimaryPlanNodeKind.Blit or
            EVulkanPrimaryPlanNodeKind.IndirectDraw or
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount or
            EVulkanPrimaryPlanNodeKind.TransformFeedback;

    private static bool IsNestedUiPipelineOp(in FrameOpContext context)
    {
        if (context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
            return true;

        if (context.PassMetadata is not { } metadata)
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
        EVulkanPrimaryPlanNodeKind? runKind = null;
        FrameOpContext runContext = default;

        for (int i = 0; i < ops.Length; i++)
        {
            ref readonly FrameOperationHeader header = ref ops.GetHeader(i);
            ref readonly FrameOpContext context = ref ops.GetContext(i);
            if (!TryResolveSecondaryCommandFamily(header.OpCode, out EVulkanSecondaryCommandFamily family))
            {
                // Ineligible ops break the current run.
                FinalizeRun(i);
                continue;
            }

            int passIndex = header.PassIndex;
            int targetIdentity = ops.GetTarget(i)?.GetHashCode() ?? 0;
            int schedulingIdentity = context.SchedulingIdentity;

            if (runStart < 0)
            {
                runStart = i;
                runPassIndex = passIndex;
                runTargetIdentity = targetIdentity;
                runSchedulingIdentity = schedulingIdentity;
                runFamily = family;
                runKind = header.OpCode;
                runContext = context;
                continue;
            }

            // Runs must remain homogeneous to be safely co-recorded.
            bool sameBucket =
                runKind == header.OpCode &&
                runPassIndex == passIndex &&
                runTargetIdentity == targetIdentity &&
                runSchedulingIdentity == schedulingIdentity &&
                runFamily == family &&
                FrameOpContextCompatibility.AreRecordingCompatible(runContext, context);

            if (!sameBucket)
            {
                // Close previous run and start a new compatible run at i.
                FinalizeRun(i);
                runStart = i;
                runPassIndex = passIndex;
                runTargetIdentity = targetIdentity;
                runSchedulingIdentity = schedulingIdentity;
                runFamily = family;
                runKind = header.OpCode;
                runContext = context;
            }
        }

        FinalizeRun(ops.Length);
        void FinalizeRun(int runEndExclusive)
        {
            if (runStart < 0 || runKind is null)
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
                    GetOperationType(runKind.Value),
                    runContext));
            }

            runStart = -1;
            runPassIndex = int.MinValue;
            runTargetIdentity = int.MinValue;
            runSchedulingIdentity = int.MinValue;
            runFamily = default;
            runKind = null;
            runContext = default;
        }
    }

    /// <summary>
    /// Determines whether an op type participates in secondary command recording buckets.
    /// </summary>
    private static bool TryResolveSecondaryCommandFamily(
        EVulkanPrimaryPlanNodeKind opCode,
        out EVulkanSecondaryCommandFamily family)
    {
        switch (opCode)
        {
            case EVulkanPrimaryPlanNodeKind.ComputeDispatch:
            case EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect:
                family = EVulkanSecondaryCommandFamily.Compute;
                return true;
            case EVulkanPrimaryPlanNodeKind.MemoryBarrier:
                family = EVulkanSecondaryCommandFamily.Synchronization;
                return true;
            case EVulkanPrimaryPlanNodeKind.BufferCopy:
                family = EVulkanSecondaryCommandFamily.Transfer;
                return true;
            case EVulkanPrimaryPlanNodeKind.Query:
                family = EVulkanSecondaryCommandFamily.Query;
                return true;
            default:
                family = default;
                return false;
        }
    }

    private static Type GetOperationType(EVulkanPrimaryPlanNodeKind opCode)
        => opCode switch
        {
            EVulkanPrimaryPlanNodeKind.ComputeDispatch => typeof(ComputeDispatchOp),
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect => typeof(ComputeDispatchIndirectOp),
            EVulkanPrimaryPlanNodeKind.MemoryBarrier => typeof(MemoryBarrierOp),
            EVulkanPrimaryPlanNodeKind.BufferCopy => typeof(BufferCopyOp),
            EVulkanPrimaryPlanNodeKind.Query => typeof(QueryOp),
            _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
        };

}

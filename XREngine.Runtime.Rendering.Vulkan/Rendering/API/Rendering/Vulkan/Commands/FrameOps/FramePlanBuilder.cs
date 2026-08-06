using System.Threading;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Thread-owned, frame-slot-ring builder for immutable <see cref="FramePlan"/>
/// publications. Storage grows only when a new high-water mark is observed and
/// is reused by the same frame slot after warmup.
/// </summary>
internal sealed class FramePlanBuilder
{
    private sealed class Slot
    {
        internal readonly ViewSetPlan ViewSet = new();
        internal readonly FramePlan Plan;
        internal readonly ExactLengthBufferCache<FrameOp> OperationBuffers = new();
        internal readonly ExactLengthBufferCache<FrameOp> DynamicOverlayOperationBuffers = new();
        internal FrameOp[] Operations = Array.Empty<FrameOp>();
        internal FrameOp[] DynamicOverlayOperations = Array.Empty<FrameOp>();
        internal FrameOp[] OperationOrderingScratch = new FrameOp[64];
        internal FrameOp[] DynamicOverlayOrderingScratch = new FrameOp[16];
        internal int[] OperationOrderScratch = new int[64];
        internal int[] OperationDependencyScratch = new int[64];
        internal int[] OperationTopologicalOrderScratch = new int[64];
        internal int[] DynamicOverlayOperationOrderScratch = new int[16];
        internal OutputRequest[] Outputs = new OutputRequest[8];
        internal int[] OutputExecutionRanks = new int[8];
        internal RenderOutputDagNodeDescriptor[] OutputExecutionNodes =
            new RenderOutputDagNodeDescriptor[32];
        internal int[] OutputNodeOrderScratch = new int[32];
        internal int[] OutputNodeIndegreeScratch = new int[32];
        internal FramePlanOperationKey[] OperationKeys = new FramePlanOperationKey[64];

        internal Slot() => Plan = new FramePlan(ViewSet);
    }

    [ThreadStatic]
    private static FramePlanBuilder? s_currentThreadBuilder;

    private Slot[] _slots = [new(), new(), new(), new()];
    private Slot[] _retiredSlots = new Slot[4];
    private int _retiredSlotCount;
    private readonly RenderOutputGraphPlanner _outputGraphPlanner = new();
    private static long s_nextGeneration;

    internal static FramePlanBuilder GetCurrentThread()
        => s_currentThreadBuilder ??= new FramePlanBuilder();

    internal FramePlan BuildAndSeal(
        int frameSlot,
        ulong plannerRevision,
        ulong staticOperationSignature,
        ulong dynamicOverlaySignature,
        FrameOp[] operations,
        FrameOp[] dynamicOverlayOperations,
        uint? openXrViewIndex = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(dynamicOverlayOperations);

        Slot slot = AcquireWritableSlot(frameSlot);
        slot.Plan.Reset();
        ulong renderFrameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        TryAttachLocatedOpenXrViews(slot.ViewSet, renderFrameId);
        EVrOutputViewKind? openXrViewKind = ResolveOpenXrViewKind(
            slot.ViewSet,
            openXrViewIndex);

        int outputCount = 0;
        int operationKeyCount = 0;
        AddPlanMetadata(slot, operations, dynamicOverlay: false, openXrViewKind, ref outputCount, ref operationKeyCount);
        AddPlanMetadata(slot, dynamicOverlayOperations, dynamicOverlay: true, openXrViewKind, ref outputCount, ref operationKeyCount);
        SortOutputs(slot.Outputs, outputCount);
        int outputExecutionNodeCount = CompileOutputGraph(
            slot,
            outputCount,
            renderFrameId);
        int operationCount = CopyOperationsInDagOrder(
            slot,
            operations,
            outputCount,
            openXrViewKind,
            dynamicOverlay: false);
        int dynamicOperationCount = CopyOperationsInDagOrder(
            slot,
            dynamicOverlayOperations,
            outputCount,
            openXrViewKind,
            dynamicOverlay: true);
        operationKeyCount = RebuildOperationKeys(
            slot,
            operationCount,
            dynamicOperationCount);

        ComputeVersionSignatures(
            operations,
            dynamicOverlayOperations,
            out ulong resourceVersionSignature,
            out ulong descriptorVersionSignature);
        ulong generation = unchecked((ulong)Interlocked.Increment(ref s_nextGeneration));
        if (generation == 0UL)
            generation = unchecked((ulong)Interlocked.Increment(ref s_nextGeneration));
        slot.Plan.Publish(
            frameSlot,
            generation,
            renderFrameId,
            plannerRevision,
            resourceVersionSignature,
            descriptorVersionSignature,
            staticOperationSignature,
            dynamicOverlaySignature,
            slot.Operations,
            operationCount,
            slot.DynamicOverlayOperations,
            dynamicOperationCount,
            slot.Outputs,
            outputCount,
            slot.OutputExecutionNodes,
            outputExecutionNodeCount,
            slot.OperationKeys,
            operationKeyCount);
        return slot.Plan;
    }

    private Slot AcquireWritableSlot(int frameSlot)
    {
        EnsureSlotCapacity(frameSlot + 1);
        Slot active = _slots[frameSlot];
        if (!active.Plan.IsPinned)
            return active;

        Slot replacement = TakeUnpinnedRetiredSlot() ?? new Slot();
        _slots[frameSlot] = replacement;
        RetireSlot(active);
        return replacement;
    }

    private void EnsureSlotCapacity(int required)
    {
        if (_slots.Length >= required)
            return;

        int previousLength = _slots.Length;
        Array.Resize(ref _slots, Math.Max(required, previousLength * 2));
        for (int index = previousLength; index < _slots.Length; index++)
            _slots[index] = new Slot();
    }

    private Slot? TakeUnpinnedRetiredSlot()
    {
        for (int index = 0; index < _retiredSlotCount; index++)
        {
            Slot candidate = _retiredSlots[index];
            if (candidate.Plan.IsPinned)
                continue;

            _retiredSlotCount--;
            _retiredSlots[index] = _retiredSlots[_retiredSlotCount];
            _retiredSlots[_retiredSlotCount] = null!;
            return candidate;
        }

        return null;
    }

    private void RetireSlot(Slot slot)
    {
        EnsureCapacity(ref _retiredSlots, _retiredSlotCount + 1);
        _retiredSlots[_retiredSlotCount++] = slot;
    }

    private static int CopyOperationsInDagOrder(
        Slot slot,
        FrameOp[] source,
        int outputCount,
        EVrOutputViewKind? openXrViewKind,
        bool dynamicOverlay)
    {
        FrameOp[] orderingScratch = dynamicOverlay
            ? slot.DynamicOverlayOrderingScratch
            : slot.OperationOrderingScratch;
        EnsureCapacity(ref orderingScratch, source.Length);
        source.CopyTo(orderingScratch, 0);
        if (dynamicOverlay)
            slot.DynamicOverlayOrderingScratch = orderingScratch;
        else
            slot.OperationOrderingScratch = orderingScratch;

        int[] orderScratch = dynamicOverlay
            ? slot.DynamicOverlayOperationOrderScratch
            : slot.OperationOrderScratch;
        EnsureCapacity(ref orderScratch, source.Length);
        for (int index = 0; index < source.Length; index++)
            orderScratch[index] = index;
        SortOperationOrder(
            slot,
            orderingScratch,
            orderScratch,
            source.Length,
            outputCount,
            openXrViewKind);
        EnsureCapacity(ref slot.OperationDependencyScratch, source.Length);
        EnsureCapacity(ref slot.OperationTopologicalOrderScratch, source.Length);
        CompileResourceDependencyOrder(
            orderingScratch,
            orderScratch,
            source.Length,
            slot.OperationDependencyScratch,
            slot.OperationTopologicalOrderScratch);
        if (dynamicOverlay)
            slot.DynamicOverlayOperationOrderScratch = orderScratch;
        else
            slot.OperationOrderScratch = orderScratch;

        FrameOp[] destination = dynamicOverlay
            ? slot.DynamicOverlayOperationBuffers.Get(source.Length)
            : slot.OperationBuffers.Get(source.Length);
        for (int index = 0; index < source.Length; index++)
            // Plans own a frozen logical snapshot rather than a frame-pool
            // object that a later producer path could reset or repurpose.
            destination[index] = orderingScratch[orderScratch[index]].CreateSealedPlanSnapshot();
        if (dynamicOverlay)
            slot.DynamicOverlayOperations = destination;
        else
            slot.Operations = destination;

        return source.Length;
    }

    private static void SortOperationOrder(
        Slot slot,
        FrameOp[] operations,
        int[] order,
        int operationCount,
        int outputCount,
        EVrOutputViewKind? openXrViewKind)
    {
        for (int index = 1; index < operationCount; index++)
        {
            int candidate = order[index];
            int candidateRank = GetOutputRank(
                slot,
                operations[candidate],
                outputCount,
                openXrViewKind);
            int insertionIndex = index;
            while (insertionIndex > 0)
            {
                int prior = order[insertionIndex - 1];
                int priorRank = GetOutputRank(
                    slot,
                    operations[prior],
                    outputCount,
                    openXrViewKind);
                if (priorRank < candidateRank ||
                    (priorRank == candidateRank && prior < candidate))
                {
                    break;
                }

                order[insertionIndex] = prior;
                insertionIndex--;
            }

            order[insertionIndex] = candidate;
        }
    }

    private static void CompileResourceDependencyOrder(
        FrameOp[] operations,
        int[] preferredOrder,
        int operationCount,
        int[] indegree,
        int[] destination)
    {
        for (int index = 0; index < operationCount; index++)
        {
            indegree[index] = 0;
            FrameOpResourceUseList uses = operations[index].ResourceUses;
            for (int useIndex = 0; useIndex < uses.Count; useIndex++)
            {
                FrameOpResourceUse use = uses[useIndex];
                if ((use.Access & EFrameOpResourceAccess.Read) != 0 &&
                    FindUniqueProducer(operations, operationCount, index, use) < 0 &&
                    (use.Access & EFrameOpResourceAccess.Imported) == 0)
                {
                    throw new InvalidOperationException(
                        "Frame operation reads a resource with no producer or imported declaration.");
                }
            }
            for (int candidate = 0; candidate < operationCount; candidate++)
                if (candidate != index &&
                    DependsOn(operations, operationCount, index, candidate))
                    indegree[index]++;
        }

        for (int outputIndex = 0; outputIndex < operationCount; outputIndex++)
        {
            int selected = -1;
            for (int priority = 0; priority < operationCount; priority++)
            {
                int candidate = preferredOrder[priority];
                if (candidate >= 0 && indegree[candidate] == 0)
                {
                    selected = candidate;
                    preferredOrder[priority] = -1;
                    break;
                }
            }
            if (selected < 0)
                throw new InvalidOperationException("Frame operation resource dependency graph contains a cycle.");

            destination[outputIndex] = selected;
            indegree[selected] = -1;
            for (int consumer = 0; consumer < operationCount; consumer++)
            {
                if (indegree[consumer] <= 0 || !DependsOn(operations, operationCount, consumer, selected))
                    continue;
                indegree[consumer]--;
            }
        }

        Array.Copy(destination, preferredOrder, operationCount);
    }

    private static bool DependsOn(
        FrameOp[] operations,
        int operationCount,
        int consumer,
        int producer)
    {
        FrameOpResourceUseList uses = operations[consumer].ResourceUses;
        for (int useIndex = 0; useIndex < uses.Count; useIndex++)
        {
            FrameOpResourceUse use = uses[useIndex];
            if ((use.Access & EFrameOpResourceAccess.Read) != 0 &&
                FindUniqueProducer(operations, operationCount, consumer, use) == producer)
                return true;
            if ((use.Access & EFrameOpResourceAccess.Write) != 0 &&
                FindPreviousWriter(operations, consumer, use) == producer)
                return true;
        }
        return false;
    }

    private static int FindUniqueProducer(
        FrameOp[] operations,
        int operationCount,
        int consumer,
        in FrameOpResourceUse read)
    {
        for (int candidate = consumer - 1; candidate >= 0; candidate--)
        {
            FrameOpResourceUseList uses = operations[candidate].ResourceUses;
            for (int useIndex = 0; useIndex < uses.Count; useIndex++)
            {
                FrameOpResourceUse write = uses[useIndex];
                if (write.ResourceId != read.ResourceId || write.Version != read.Version ||
                    (write.Access & EFrameOpResourceAccess.Write) == 0)
                {
                    continue;
                }
                // The operation stream is the source of version chronology;
                // a later op cannot produce an earlier op's read.
                return candidate;
            }
        }
        return -1;
    }

    private static int FindPreviousWriter(
        FrameOp[] operations,
        int consumer,
        in FrameOpResourceUse write)
    {
        for (int candidate = consumer - 1; candidate >= 0; candidate--)
        {
            FrameOpResourceUseList uses = operations[candidate].ResourceUses;
            for (int useIndex = 0; useIndex < uses.Count; useIndex++)
            {
                FrameOpResourceUse prior = uses[useIndex];
                if (prior.ResourceId == write.ResourceId && prior.Version == write.Version &&
                    (prior.Access & EFrameOpResourceAccess.Write) != 0)
                {
                    return candidate;
                }
            }
        }
        return -1;
    }

    private static int GetOutputRank(
        Slot slot,
        FrameOp operation,
        int outputCount,
        EVrOutputViewKind? openXrViewKind)
    {
        OutputRequest request = OutputRequest.FromContext(
            operation.Context,
            openXrViewKind);
        for (int index = 0; index < outputCount; index++)
        {
            if (slot.Outputs[index].MatchesOutput(request))
                return slot.OutputExecutionRanks[index];
        }

        throw new InvalidOperationException("A frame operation has no compiled output request.");
    }

    private static int RebuildOperationKeys(
        Slot slot,
        int operationCount,
        int dynamicOperationCount)
    {
        int keyCount = 0;
        EnsureCapacity(ref slot.OperationKeys, operationCount + dynamicOperationCount);
        for (int index = 0; index < operationCount; index++)
        {
            slot.OperationKeys[keyCount++] = FramePlanOperationKey.FromOperation(
                slot.Operations[index],
                index,
                isDynamicOverlay: false);
        }
        for (int index = 0; index < dynamicOperationCount; index++)
        {
            slot.OperationKeys[keyCount++] = FramePlanOperationKey.FromOperation(
                slot.DynamicOverlayOperations[index],
                index,
                isDynamicOverlay: true);
        }

        return keyCount;
    }

    private static void AddPlanMetadata(
        Slot slot,
        FrameOp[] operations,
        bool dynamicOverlay,
        EVrOutputViewKind? openXrViewKind,
        ref int outputCount,
        ref int operationKeyCount)
    {
        for (int index = 0; index < operations.Length; index++)
        {
            FrameOp operation = operations[index];
            FrameOpContext context = operation.Context;
            slot.ViewSet.Add(context);
            EVrOutputViewKind? operationViewKind = openXrViewKind;
            if (context.ContextKind == EVulkanFrameOpContextKind.OpenXrEye &&
                slot.ViewSet.TryGetLocatedOpenXrViewKindByLogicalViewId(
                    context.LogicalViewId,
                    out EVrOutputViewKind locatedViewKind))
            {
                operationViewKind = locatedViewKind;
            }
            AddOutput(slot, OutputRequest.FromContext(context, operationViewKind), ref outputCount);
            EnsureCapacity(ref slot.OperationKeys, operationKeyCount + 1);
            slot.OperationKeys[operationKeyCount++] = FramePlanOperationKey.FromOperation(
                operation,
                index,
                dynamicOverlay);
        }
    }

    private static void AddOutput(Slot slot, in OutputRequest request, ref int outputCount)
    {
        for (int index = 0; index < outputCount; index++)
        {
            if (!slot.Outputs[index].MatchesOutput(request))
                continue;

            if (slot.Outputs[index].ProducerDependencySetId != request.ProducerDependencySetId)
            {
                throw new InvalidOperationException(
                    "Frame output dataflow is invalid: one output terminal has conflicting produced resource sets.");
            }
            if (request.ConsumerDependencySetId == 0UL ||
                slot.Outputs[index].ConsumerDependencySetId == request.ConsumerDependencySetId)
            {
                return;
            }
            if (slot.Outputs[index].ConsumerDependencySetId != 0UL)
            {
                throw new InvalidOperationException(
                    "Frame output dataflow is invalid: one output terminal consumes multiple resource sets.");
            }

            slot.Outputs[index] = request;
            return;
        }

        EnsureCapacity(ref slot.Outputs, outputCount + 1);
        slot.Outputs[outputCount++] = request;
    }

    private int CompileOutputGraph(Slot slot, int outputCount, ulong renderFrameId)
    {
        if (outputCount == 0)
            return 0;

        for (int index = 0; index < outputCount; index++)
        {
            if (!_outputGraphPlanner.Reserve(slot.Outputs[index].ToGraphRequest(renderFrameId)))
            {
                throw new InvalidOperationException(
                    "Frame output planning exceeded the configured DAG reservation capacity or used an invalid frame id.");
            }
        }

        for (int index = 0; index < outputCount; index++)
        {
            ref readonly OutputRequest output = ref slot.Outputs[index];
            RenderOutputRequest request = output.ToGraphRequest(renderFrameId);
            bool independentDesktopScene = output.OutputKind != EFrameOutputKind.DesktopMirror;
            int terminalNode = _outputGraphPlanner.Plan(
                request,
                isDue: true,
                independentDesktopScene,
                EFrameOutputKind.OpenXREyeSubmit);
            if (terminalNode < 0)
            {
                throw new InvalidOperationException(
                    "Frame output planning exceeded the configured DAG capacity or used an invalid frame id.");
            }
        }

        AddExplicitOutputDependencies(slot, outputCount, renderFrameId);

        RenderOutputDag graph = _outputGraphPlanner.Graph;
        EnsureCapacity(ref slot.OutputExecutionNodes, graph.NodeCount);
        EnsureCapacity(ref slot.OutputNodeOrderScratch, graph.NodeCount);
        EnsureCapacity(ref slot.OutputNodeIndegreeScratch, graph.SlotCount);
        if (!graph.TryCompileDeterministicOrder(
                slot.OutputNodeOrderScratch,
                slot.OutputNodeIndegreeScratch,
                out int executionNodeCount,
                out ERenderOutputDagCompilationFailure failure))
        {
            throw new InvalidOperationException(
                $"Frame output DAG is not recordable: {failure}.");
        }

        for (int index = 0; index < executionNodeCount; index++)
        {
            slot.OutputExecutionNodes[index] = graph.GetNode(
                slot.OutputNodeOrderScratch[index]);
        }

        EnsureCapacity(ref slot.OutputExecutionRanks, outputCount);
        for (int outputIndex = 0; outputIndex < outputCount; outputIndex++)
        {
            ulong outputId = slot.Outputs[outputIndex].StableOutputId;
            int rank = int.MaxValue;
            for (int nodeIndex = 0; nodeIndex < executionNodeCount; nodeIndex++)
            {
                if (slot.OutputExecutionNodes[nodeIndex].StableOutputKey == outputId)
                    rank = Math.Min(rank, nodeIndex);
            }
            if (rank == int.MaxValue)
            {
                throw new InvalidOperationException(
                    "A compiled output request has no executable DAG terminal.");
            }

            slot.OutputExecutionRanks[outputIndex] = rank;
        }

        return executionNodeCount;
    }

    private void AddExplicitOutputDependencies(
        Slot slot,
        int outputCount,
        ulong renderFrameId)
    {
        for (int dependentIndex = 0; dependentIndex < outputCount; dependentIndex++)
        {
            ref readonly OutputRequest dependent = ref slot.Outputs[dependentIndex];
            if (dependent.ConsumerDependencySetId == 0UL)
                continue;

            int producerIndex = -1;
            for (int candidateIndex = 0; candidateIndex < outputCount; candidateIndex++)
            {
                if (slot.Outputs[candidateIndex].ProducerDependencySetId !=
                    dependent.ConsumerDependencySetId)
                {
                    continue;
                }
                if (producerIndex >= 0)
                {
                    throw new InvalidOperationException(
                        "Frame output dataflow is ambiguous: multiple producers publish the requested dependency set.");
                }

                producerIndex = candidateIndex;
            }

            if (producerIndex < 0)
            {
                throw new InvalidOperationException(
                    "Frame output dataflow is incomplete: no producer publishes the requested dependency set.");
            }

            if (!_outputGraphPlanner.TryAddOutputDependency(
                    slot.Outputs[producerIndex].ToGraphRequest(renderFrameId),
                    dependent.ToGraphRequest(renderFrameId),
                    out string? failureReason))
            {
                throw new InvalidOperationException(
                    $"Frame output dataflow dependency is invalid: {failureReason}.");
            }
        }
    }

    private static void TryAttachLocatedOpenXrViews(ViewSetPlan viewSet, ulong renderFrameId)
    {
        if (renderFrameId != 0UL &&
            RenderFrameViewSetPublication.TryGet(renderFrameId, out RenderFrameViewSet locatedViews))
        {
            viewSet.SetLocatedOpenXrViews(locatedViews);
        }
    }

    private static EVrOutputViewKind? ResolveOpenXrViewKind(
        ViewSetPlan viewSet,
        uint? openXrViewIndex)
    {
        if (openXrViewIndex is not uint viewIndex)
            return null;

        if (viewSet.TryGetLocatedOpenXrViewKind(viewIndex, out EVrOutputViewKind kind))
            return kind;

        // Located views are not available during early startup/prewarm. Keep
        // the conventional stereo mapping deterministic until they publish.
        return viewIndex == 0u
            ? EVrOutputViewKind.LeftEye
            : EVrOutputViewKind.RightEye;
    }

    private static void SortOutputs(OutputRequest[] outputs, int count)
    {
        for (int index = 1; index < count; index++)
        {
            OutputRequest candidate = outputs[index];
            int insertionIndex = index;
            while (insertionIndex > 0 &&
                   OutputRequest.CompareDeterministically(
                       candidate,
                       outputs[insertionIndex - 1]) < 0)
            {
                outputs[insertionIndex] = outputs[insertionIndex - 1];
                insertionIndex--;
            }

            outputs[insertionIndex] = candidate;
        }
    }

    private static void EnsureCapacity<T>(ref T[] storage, int required)
    {
        if (storage.Length >= required)
            return;

        Array.Resize(ref storage, Math.Max(required, storage.Length * 2));
    }

    private static void ComputeVersionSignatures(
        FrameOp[] operations,
        FrameOp[] dynamicOverlayOperations,
        out ulong resourceVersionSignature,
        out ulong descriptorVersionSignature)
    {
        resourceVersionSignature = 1469598103934665603UL;
        descriptorVersionSignature = 1099511628211UL;
        AddVersionComponents(operations, ref resourceVersionSignature, ref descriptorVersionSignature);
        AddVersionComponents(dynamicOverlayOperations, ref resourceVersionSignature, ref descriptorVersionSignature);
    }

    private static void AddVersionComponents(
        FrameOp[] operations,
        ref ulong resourceVersionSignature,
        ref ulong descriptorVersionSignature)
    {
        for (int index = 0; index < operations.Length; index++)
        {
            FrameOpContext context = operations[index].Context;
            Add(ref resourceVersionSignature, context.ResourceGeneration);
            Add(ref resourceVersionSignature, context.RecordingFingerprint);
            Add(ref descriptorVersionSignature, context.DescriptorGeneration);
            Add(ref descriptorVersionSignature, context.RecordingFingerprint);
        }
    }

    private static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }

    private sealed class ExactLengthBufferCache<T>
    {
        // A transient spike must not permanently retain an unbounded set of
        // exact-length plan buffers. Four sizes cover the slot ring's normal
        // steady state while keeping future allocation behavior deterministic.
        private const int MaximumCachedLengths = 8;
        private T[][] _buffers = new T[4][];
        private int _bufferCount;

        internal T[] Get(int length)
        {
            for (int index = 0; index < _bufferCount; index++)
            {
                T[] buffer = _buffers[index];
                if (buffer.Length == length)
                    return buffer;
            }

            T[] created = length == 0 ? Array.Empty<T>() : new T[length];
            if (_bufferCount == MaximumCachedLengths)
            {
                int evictionIndex = 0;
                for (int index = 1; index < _bufferCount; index++)
                    if (_buffers[index].Length < _buffers[evictionIndex].Length)
                        evictionIndex = index;
                _buffers[evictionIndex] = created;
                return created;
            }
            EnsureCapacity(ref _buffers, _bufferCount + 1);
            _buffers[_bufferCount++] = created;
            return created;
        }
    }
}

using System.Threading;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Render-graph-owned, frame-slot-ring builder for immutable
/// <see cref="FramePlan"/> publications. Storage grows only when a new
/// high-water mark is observed and is reused by the same frame slot after
/// warmup.
/// </summary>
internal sealed class FramePlanBuilder
{
    private readonly record struct ResourceVersionKey(ulong ResourceId, ulong Version);
    private readonly VulkanFrameOperationScheduler _frameScheduler = new();

    private sealed class Slot
    {
        internal readonly ViewSetPlan ViewSet = new();
        internal readonly FramePlan Plan;
        internal readonly FrameOperationStream Operations = new();
        internal readonly FrameOperationStream DynamicOverlayOperations = new();
        internal readonly FrameOperationStream TextureUploadOperations = new();
        internal readonly FrameOperationIngress StaticIngress = new();
        internal readonly FrameOperationIngress DynamicIngress = new();
        internal readonly FrameOperationIngress TextureUploadIngress = new();
        // Authoring arrays are never published. The slot's operation streams
        // are the plan-owned representation after numeric ordering.
        internal int[] OperationOrderScratch = new int[64];
        internal int[] OperationDependencyScratch = new int[64];
        internal int[] OperationTopologicalOrderScratch = new int[64];
        internal int[] OperationPriorityScratch = new int[64];
        internal int[] OperationReadyHeapScratch = new int[64];
        internal int[] DependencyFirstEdgeScratch = new int[64];
        internal int[] DependencyEdgeConsumerScratch = new int[256];
        internal int[] DependencyEdgeNextScratch = new int[256];
        internal readonly Dictionary<ResourceVersionKey, int> LastResourceWriters = new(256);
        internal int[] DynamicOverlayOperationOrderScratch = new int[16];
        internal OutputRequest[] Outputs = new OutputRequest[8];
        internal int[] OutputExecutionRanks = new int[8];
        internal RenderOutputDagNodeDescriptor[] OutputExecutionNodes =
            new RenderOutputDagNodeDescriptor[32];
        internal int[] OutputNodeOrderScratch = new int[32];
        internal int[] OutputNodeIndegreeScratch = new int[32];
        internal FramePlanOperationKey[] OperationKeys = new FramePlanOperationKey[64];
        internal VulkanFrameOpPlannerStateKey[] StaticPlannerContextKeys = new VulkanFrameOpPlannerStateKey[8];
        internal FrameOpContext[] StaticPlannerContexts = new FrameOpContext[8];
        internal VulkanRenderGraphPlan[] StaticPlannerContextPlans = new VulkanRenderGraphPlan[8];

        internal Slot() => Plan = new FramePlan(ViewSet);
    }

    private Slot[] _slots = [new(), new(), new(), new()];
    private Slot[] _retiredSlots = new Slot[4];
    private int _retiredSlotCount;
    private readonly RenderOutputGraphPlanner _outputGraphPlanner = new();
    private long _nextGeneration;
    internal VulkanFrameOperationScheduler FrameScheduler => _frameScheduler;

    internal FramePlan BuildAndSeal(
        int frameSlot,
        ulong plannerRevision,
        ulong staticOperationSignature,
        ulong dynamicOverlaySignature,
        FrameOp[] operations,
        FrameOp[] dynamicOverlayOperations,
        in VulkanFramePlanRenderGraphAuthority renderGraphAuthority,
        uint? openXrViewIndex = null,
        FrameOp[]? textureUploadOperations = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(dynamicOverlayOperations);
        textureUploadOperations ??= [];

        Slot slot = AcquireWritableSlot(frameSlot);
        slot.Plan.Reset();
        ulong renderFrameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        TryAttachLocatedOpenXrViews(slot.ViewSet, renderFrameId);
        EVrOutputViewKind? openXrViewKind = ResolveOpenXrViewKind(
            slot.ViewSet,
            openXrViewIndex);
        slot.StaticIngress.Populate(operations);
        slot.DynamicIngress.Populate(dynamicOverlayOperations);
        slot.TextureUploadIngress.Populate(textureUploadOperations);
        // This is the sole object-to-stream boundary. Everything below,
        // including output ordering and resource-DAG compilation, consumes
        // numeric headers and dense typed payload streams only.
        slot.Operations.Lower(slot.StaticIngress);
        slot.DynamicOverlayOperations.Lower(slot.DynamicIngress);
        slot.TextureUploadOperations.Lower(slot.TextureUploadIngress);
        // Ordering is deliberately inside the sealed numeric boundary. No DAG,
        // metadata, worker, or recording stage can observe an authoring FrameOp.
        VulkanCompiledRenderGraph graph = renderGraphAuthority.FallbackPlan.CompiledGraph;
        _frameScheduler.SortLoweredOperations(slot.Operations, graph);
        _frameScheduler.SortLoweredOperations(slot.DynamicOverlayOperations, graph);
        // The published signature is derived from the same sealed numeric
        // streams that recording and worker scheduling consume. The ingress
        // values remain call-site cache hints only until those paths migrate.
        staticOperationSignature = VulkanFrameOperationSemantics.ComputeFrameOpsSignature(
            new FrameOperationSequence(slot.Operations));
        dynamicOverlaySignature = VulkanFrameOperationSemantics.ComputeFrameOpsSignature(
            new FrameOperationSequence(slot.DynamicOverlayOperations));

        int outputCount = 0;
        int operationKeyCount = 0;
        AddPlanMetadata(slot, slot.Operations, dynamicOverlay: false, openXrViewKind, ref outputCount, ref operationKeyCount);
        AddPlanMetadata(slot, slot.DynamicOverlayOperations, dynamicOverlay: true, openXrViewKind, ref outputCount, ref operationKeyCount);
        SortOutputs(slot.Outputs, outputCount);
        int outputExecutionNodeCount = CompileOutputGraph(
            slot,
            outputCount,
            renderFrameId);
        int operationCount = CopyOperationsInDagOrder(
            slot,
            slot.Operations,
            outputCount,
            openXrViewKind,
            dynamicOverlay: false);
        int dynamicOperationCount = CopyOperationsInDagOrder(
            slot,
            slot.DynamicOverlayOperations,
            outputCount,
            openXrViewKind,
            dynamicOverlay: true);
        operationKeyCount = RebuildOperationKeys(
            slot,
            operationCount,
            dynamicOperationCount);
        int staticPlannerContextKeyCount = CollectStaticPlannerContextKeys(slot);
        ulong renderGraphPlanSignature = ResolveStaticPlannerContextPlans(
            slot,
            staticPlannerContextKeyCount,
            in renderGraphAuthority);

        VulkanFrameOperationSignature.ComputeVersionSignatures(
            slot.Operations,
            slot.DynamicOverlayOperations,
            out ulong resourceVersionSignature,
            out ulong descriptorVersionSignature);
        ulong generation = unchecked((ulong)Interlocked.Increment(ref _nextGeneration));
        if (generation == 0UL)
            generation = unchecked((ulong)Interlocked.Increment(ref _nextGeneration));
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
            slot.DynamicOverlayOperations,
            slot.TextureUploadOperations,
            slot.Outputs,
            outputCount,
            slot.OutputExecutionNodes,
            outputExecutionNodeCount,
            slot.OperationKeys,
            operationKeyCount,
            slot.StaticPlannerContextKeys,
            slot.StaticPlannerContexts,
            slot.StaticPlannerContextPlans,
            staticPlannerContextKeyCount,
            renderGraphPlanSignature);
        return slot.Plan;
    }

    private static int CollectStaticPlannerContextKeys(Slot slot)
    {
        int keyCount = 0;
        for (int operationIndex = 0; operationIndex < slot.Operations.Count; operationIndex++)
        {
            ref readonly FrameOpContext context = ref slot.Operations.GetContext(operationIndex);
            if (context.ResourceRegistry is null && context.PassMetadata is not { Count: > 0 })
                continue;

            VulkanFrameOpPlannerStateKey key =
                VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(context);
            bool exists = false;
            for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
            {
                if (!slot.StaticPlannerContextKeys[keyIndex].Equals(key))
                    continue;

                exists = true;
                break;
            }

            if (exists)
                continue;

            if (keyCount == slot.StaticPlannerContextKeys.Length)
            {
                int newCapacity = Math.Max(8, slot.StaticPlannerContextKeys.Length * 2);
                Array.Resize(ref slot.StaticPlannerContextKeys, newCapacity);
                Array.Resize(ref slot.StaticPlannerContexts, newCapacity);
            }

            slot.StaticPlannerContextKeys[keyCount] = key;
            slot.StaticPlannerContexts[keyCount] = context;
            keyCount++;
        }

        return keyCount;
    }

    private static ulong ResolveStaticPlannerContextPlans(
        Slot slot,
        int keyCount,
        in VulkanFramePlanRenderGraphAuthority authority)
    {
        if (slot.StaticPlannerContextPlans.Length < keyCount)
            Array.Resize(ref slot.StaticPlannerContextPlans, slot.StaticPlannerContextKeys.Length);

        FrameOpSignatureHasher signature = new();
        signature.Add(keyCount);
        for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
        {
            VulkanFrameOpPlannerStateKey key = slot.StaticPlannerContextKeys[keyIndex];
            if (!authority.TryResolve(key, keyCount, out VulkanRenderGraphPlan plan))
            {
                throw new VulkanPlanPreconditionException(
                    $"Frame plan has no frozen render-graph publication for context " +
                    $"kind={key.ContextKind} pipe={key.PipelineIdentity} viewport={key.ViewportIdentity} " +
                    $"resourceGeneration={key.ResourceGeneration}.");
            }

            slot.StaticPlannerContextPlans[keyIndex] = plan;
            signature.Add((int)key.ContextKind);
            signature.Add(key.PipelineIdentity);
            signature.Add(key.ViewportIdentity);
            signature.Add(key.LogicalViewId);
            signature.Add(key.ResourceGeneration);
            signature.Add(plan.Revision);
            signature.Add(plan.CompatibilityIdentity);
            signature.Add(plan.Barriers.Generation);
        }

        return signature.ToHash();
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
        FrameOperationStream source,
        int outputCount,
        EVrOutputViewKind? openXrViewKind,
        bool dynamicOverlay)
    {
        int[] orderScratch = dynamicOverlay
            ? slot.DynamicOverlayOperationOrderScratch
            : slot.OperationOrderScratch;
        EnsureCapacity(ref orderScratch, source.Count);
        for (int index = 0; index < source.Count; index++)
            orderScratch[index] = index;
        SortOperationOrder(
            slot,
            source,
            orderScratch,
            source.Count,
            outputCount,
            openXrViewKind);
        EnsureCapacity(ref slot.OperationDependencyScratch, source.Count);
        EnsureCapacity(ref slot.OperationTopologicalOrderScratch, source.Count);
        CompileResourceDependencyOrder(slot, source, orderScratch, source.Count);
        if (dynamicOverlay)
            slot.DynamicOverlayOperationOrderScratch = orderScratch;
        else
            slot.OperationOrderScratch = orderScratch;

        source.Reorder(orderScratch.AsSpan(0, source.Count));

        return source.Count;
    }

    private static void SortOperationOrder(
        Slot slot,
        FrameOperationStream operations,
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
                operations.GetContext(candidate),
                outputCount,
                openXrViewKind);
            int insertionIndex = index;
            while (insertionIndex > 0)
            {
                int prior = order[insertionIndex - 1];
                int priorRank = GetOutputRank(
                    slot,
                operations.GetContext(prior),
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
        Slot slot,
        FrameOperationStream operations,
        int[] preferredOrder,
        int operationCount)
    {
        int[] indegree = slot.OperationDependencyScratch;
        int[] destination = slot.OperationTopologicalOrderScratch;
        EnsureCapacity(ref slot.OperationPriorityScratch, operationCount);
        EnsureCapacity(ref slot.OperationReadyHeapScratch, operationCount);
        EnsureCapacity(ref slot.DependencyFirstEdgeScratch, operationCount);
        int[] priorities = slot.OperationPriorityScratch;
        int[] readyHeap = slot.OperationReadyHeapScratch;
        int[] firstEdges = slot.DependencyFirstEdgeScratch;
        Array.Fill(firstEdges, -1, 0, operationCount);
        slot.LastResourceWriters.Clear();
        int edgeCount = 0;
        Span<int> dependencies = stackalloc int[FrameOpResourceUseBuffer.Capacity];

        for (int index = 0; index < operationCount; index++)
        {
            indegree[index] = 0;
            ref readonly FrameOpResourceUseList uses =
                ref operations.GetResourceUses(index);
            int dependencyCount = 0;
            for (int useIndex = 0; useIndex < uses.Count; useIndex++)
            {
                FrameOpResourceUse use = uses[useIndex];
                ResourceVersionKey key = new(use.ResourceId, use.Version);
                bool hasProducer = slot.LastResourceWriters.TryGetValue(key, out int producer);
                if ((use.Access & EFrameOpResourceAccess.Read) != 0 &&
                    !hasProducer &&
                    (use.Access & EFrameOpResourceAccess.Imported) == 0)
                {
                    throw new InvalidOperationException(
                        "Frame operation reads a resource with no producer or imported declaration.");
                }

                if (!hasProducer || producer == index ||
                    ((use.Access & (EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Write)) == 0))
                {
                    continue;
                }

                bool duplicate = false;
                for (int dependencyIndex = 0; dependencyIndex < dependencyCount; dependencyIndex++)
                {
                    if (dependencies[dependencyIndex] != producer)
                        continue;

                    duplicate = true;
                    break;
                }
                if (duplicate)
                    continue;

                dependencies[dependencyCount++] = producer;
                AddResourceDependencyEdge(slot, producer, index, ref edgeCount);
                indegree[index]++;
            }

            // Publish writes only after every access in this operation has
            // resolved against the preceding stream. A read/write use must
            // depend on the prior producer, never on itself.
            for (int useIndex = 0; useIndex < uses.Count; useIndex++)
            {
                FrameOpResourceUse use = uses[useIndex];
                if ((use.Access & EFrameOpResourceAccess.Write) != 0)
                    slot.LastResourceWriters[new(use.ResourceId, use.Version)] = index;
            }
        }

        for (int priority = 0; priority < operationCount; priority++)
            priorities[preferredOrder[priority]] = priority;

        int readyCount = 0;
        for (int operation = 0; operation < operationCount; operation++)
            if (indegree[operation] == 0)
                PushReadyOperation(readyHeap, ref readyCount, operation, priorities);

        for (int outputIndex = 0; outputIndex < operationCount; outputIndex++)
        {
            if (readyCount == 0)
                throw new InvalidOperationException("Frame operation resource dependency graph contains a cycle.");

            int selected = PopReadyOperation(readyHeap, ref readyCount, priorities);
            destination[outputIndex] = selected;
            indegree[selected] = -1;
            for (int edge = firstEdges[selected]; edge >= 0; edge = slot.DependencyEdgeNextScratch[edge])
            {
                int consumer = slot.DependencyEdgeConsumerScratch[edge];
                if (indegree[consumer] <= 0)
                    continue;

                if (--indegree[consumer] == 0)
                    PushReadyOperation(readyHeap, ref readyCount, consumer, priorities);
            }
        }

        Array.Copy(destination, preferredOrder, operationCount);
    }

    private static void AddResourceDependencyEdge(
        Slot slot,
        int producer,
        int consumer,
        ref int edgeCount)
    {
        EnsureCapacity(ref slot.DependencyEdgeConsumerScratch, edgeCount + 1);
        EnsureCapacity(ref slot.DependencyEdgeNextScratch, edgeCount + 1);
        slot.DependencyEdgeConsumerScratch[edgeCount] = consumer;
        slot.DependencyEdgeNextScratch[edgeCount] = slot.DependencyFirstEdgeScratch[producer];
        slot.DependencyFirstEdgeScratch[producer] = edgeCount++;
    }

    private static void PushReadyOperation(
        int[] heap,
        ref int count,
        int operation,
        int[] priorities)
    {
        int index = count++;
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            int parentOperation = heap[parent];
            if (priorities[parentOperation] <= priorities[operation])
                break;

            heap[index] = parentOperation;
            index = parent;
        }

        heap[index] = operation;
    }

    private static int PopReadyOperation(int[] heap, ref int count, int[] priorities)
    {
        int result = heap[0];
        int replacement = heap[--count];
        int index = 0;
        while (true)
        {
            int left = (index * 2) + 1;
            if (left >= count)
                break;

            int right = left + 1;
            int child = right < count && priorities[heap[right]] < priorities[heap[left]]
                ? right
                : left;
            if (priorities[replacement] <= priorities[heap[child]])
                break;

            heap[index] = heap[child];
            index = child;
        }

        if (count > 0)
            heap[index] = replacement;
        return result;
    }

    private static int GetOutputRank(
        Slot slot,
        in FrameOpContext context,
        int outputCount,
        EVrOutputViewKind? openXrViewKind)
    {
        OutputRequest request = OutputRequest.FromContext(
            context,
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
            slot.OperationKeys[keyCount++] = FramePlanOperationKey.FromHeader(
                slot.Operations.GetHeader(index),
                slot.Operations.GetContext(index),
                isDynamicOverlay: false);
        }
        for (int index = 0; index < dynamicOperationCount; index++)
        {
            slot.OperationKeys[keyCount++] = FramePlanOperationKey.FromHeader(
                slot.DynamicOverlayOperations.GetHeader(index),
                slot.DynamicOverlayOperations.GetContext(index),
                isDynamicOverlay: true);
        }

        return keyCount;
    }

    private static void AddPlanMetadata(
        Slot slot,
        FrameOperationStream operations,
        bool dynamicOverlay,
        EVrOutputViewKind? openXrViewKind,
        ref int outputCount,
        ref int operationKeyCount)
    {
        for (int index = 0; index < operations.Count; index++)
        {
            FrameOpContext context = operations.GetContext(index);
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
            slot.OperationKeys[operationKeyCount++] = FramePlanOperationKey.FromHeader(
                operations.GetHeader(index),
                context,
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

}

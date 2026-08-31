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
    private const int FrameSlotCapacity = 4;
    private const int StaticOperationCapacity = VulkanAcceptedFramePlan.StaticCapacity;
    private const int DynamicOperationCapacity = VulkanAcceptedFramePlan.UiCapacity;
    private const int TextureUploadOperationCapacity = VulkanAcceptedFramePlan.UploadCapacity;
    private const int TotalPublishedOperationCapacity =
        StaticOperationCapacity + DynamicOperationCapacity;
    private const int ResourceUseCapacity = 65536;
    private const int ResourceDependencyEdgeCapacity = 65536;
    private const int GeneralStaticPayloadCapacity = 2048;
    private const int GeneralDynamicPayloadCapacity = 512;
    private const int GeneralUploadPayloadCapacity = 256;
    private const int OutputCapacity = 512;
    private const int OutputNodeCapacity = 4096;
    private const int PlannerContextCapacity = 256;
    private const int ViewCapacity = 256;

    private readonly record struct ResourceVersionKey(ulong ResourceId, ulong Version);
    private readonly VulkanFrameOperationScheduler _frameScheduler = new();

    private sealed class Slot
    {
        internal readonly ViewSetPlan ViewSet = new(ViewCapacity, fixedCapacity: true);
        internal readonly FramePlan Plan;
        internal readonly FrameOperationStream Operations = new(
            StaticOperationCapacity,
            ResourceUseCapacity,
            GeneralStaticPayloadCapacity,
            StaticOperationCapacity,
            texturePayloadCapacity: GeneralStaticPayloadCapacity,
            advancedVisibilityDrawCapacity:
                AdvancedPreparationOptions.Default.MaximumDraws,
            advancedVisibilityRangeCapacity:
                AdvancedPreparationOptions.Default.MaximumIndirectRanges,
            lane: EVulkanAcceptedFrameLane.MainScene);
        internal readonly FrameOperationStream DynamicOverlayOperations = new(
            DynamicOperationCapacity,
            ResourceUseCapacity / 8,
            GeneralDynamicPayloadCapacity,
            DynamicOperationCapacity,
            texturePayloadCapacity: GeneralDynamicPayloadCapacity,
            advancedVisibilityDrawCapacity: 0,
            advancedVisibilityRangeCapacity: 0,
            lane: EVulkanAcceptedFrameLane.Ui);
        internal readonly FrameOperationStream TextureUploadOperations = new(
            TextureUploadOperationCapacity,
            ResourceUseCapacity / 4,
            GeneralUploadPayloadCapacity,
            meshPayloadCapacity: GeneralUploadPayloadCapacity,
            texturePayloadCapacity: TextureUploadOperationCapacity,
            advancedVisibilityDrawCapacity: 0,
            advancedVisibilityRangeCapacity: 0,
            lane: EVulkanAcceptedFrameLane.Upload);
        internal readonly FrameOperationIngress StaticIngress = new();
        internal readonly FrameOperationIngress DynamicIngress = new();
        internal readonly FrameOperationIngress TextureUploadIngress = new();
        // Authoring arrays are never published. The slot's operation streams
        // are the plan-owned representation after numeric ordering.
        internal int[] OperationOrderScratch = new int[StaticOperationCapacity];
        internal int[] OperationDependencyScratch = new int[StaticOperationCapacity];
        internal int[] OperationTopologicalOrderScratch = new int[StaticOperationCapacity];
        internal int[] OperationPriorityScratch = new int[StaticOperationCapacity];
        internal int[] OperationReadyHeapScratch = new int[StaticOperationCapacity];
        internal int[] ResourceProducerDependencyScratch = new int[StaticOperationCapacity];
        internal int[] DependencyFirstEdgeScratch = new int[StaticOperationCapacity];
        internal int[] DependencyEdgeConsumerScratch = new int[ResourceDependencyEdgeCapacity];
        internal int[] DependencyEdgeNextScratch = new int[ResourceDependencyEdgeCapacity];
        internal readonly Dictionary<ResourceVersionKey, int> LastResourceWriters =
            new(ResourceDependencyEdgeCapacity);
        internal int[] DynamicOverlayOperationOrderScratch = new int[DynamicOperationCapacity];
        internal OutputRequest[] Outputs = new OutputRequest[OutputCapacity];
        internal RenderOutputRequest[] OutputGraphRequests = new RenderOutputRequest[OutputCapacity];
        internal RenderOutputSchedulingDecision[] OutputDecisions =
            new RenderOutputSchedulingDecision[OutputCapacity];
        internal bool[] OutputDue = new bool[OutputCapacity];
        internal bool[] OutputExecutable = new bool[OutputCapacity];
        internal int[] OutputExecutionRanks = new int[OutputCapacity];
        internal RenderOutputDagNodeDescriptor[] OutputExecutionNodes =
            new RenderOutputDagNodeDescriptor[OutputNodeCapacity];
        internal int[] OutputNodeOrderScratch = new int[OutputNodeCapacity];
        internal int[] OutputNodeIndegreeScratch = new int[OutputNodeCapacity];
        internal FramePlanOperationKey[] OperationKeys =
            new FramePlanOperationKey[TotalPublishedOperationCapacity];
        internal VulkanFrameOpPlannerStateKey[] StaticPlannerContextKeys =
            new VulkanFrameOpPlannerStateKey[PlannerContextCapacity];
        internal FrameOpContext[] StaticPlannerContexts =
            new FrameOpContext[PlannerContextCapacity];
        internal VulkanRenderGraphPlan[] StaticPlannerContextPlans =
            new VulkanRenderGraphPlan[PlannerContextCapacity];

        internal Slot() => Plan = new FramePlan(ViewSet, Operations);
    }

    private readonly Slot[] _slots = [new(), new(), new(), new()];
    private readonly Slot[] _retiredSlots = [new(), new(), new(), new()];
    private int _retiredSlotCount = FrameSlotCapacity;
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
        bool openXrImagesAcquired = false,
        FrameOp[]? textureUploadOperations = null,
        VulkanPreparedMeshIngress? preparedMeshIngress = null,
        int authoringOperationCount = -1,
        int authoringDynamicOverlayOperationCount = -1,
        int authoringTextureUploadOperationCount = -1,
        RenderOutputRequest? emptyPresentNowOutputContract = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(dynamicOverlayOperations);
        textureUploadOperations ??= [];
        authoringOperationCount = authoringOperationCount < 0
            ? operations.Length
            : authoringOperationCount;
        authoringDynamicOverlayOperationCount = authoringDynamicOverlayOperationCount < 0
            ? dynamicOverlayOperations.Length
            : authoringDynamicOverlayOperationCount;
        authoringTextureUploadOperationCount = authoringTextureUploadOperationCount < 0
            ? textureUploadOperations.Length
            : authoringTextureUploadOperationCount;
        if ((uint)authoringOperationCount > (uint)operations.Length)
            throw new ArgumentOutOfRangeException(nameof(authoringOperationCount));
        if ((uint)authoringDynamicOverlayOperationCount >
            (uint)dynamicOverlayOperations.Length)
            throw new ArgumentOutOfRangeException(
                nameof(authoringDynamicOverlayOperationCount));
        if ((uint)authoringTextureUploadOperationCount >
            (uint)textureUploadOperations.Length)
            throw new ArgumentOutOfRangeException(
                nameof(authoringTextureUploadOperationCount));

        Slot slot = AcquireWritableSlot(frameSlot);
        slot.Plan.Reset();
        ulong renderFrameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        TryAttachLocatedOpenXrViews(slot.ViewSet);
        EVrOutputViewKind? openXrViewKind = ResolveOpenXrViewKind(
            slot.ViewSet,
            openXrViewIndex);
        slot.StaticIngress.Populate(operations, authoringOperationCount);
        slot.DynamicIngress.Populate(
            dynamicOverlayOperations,
            authoringDynamicOverlayOperationCount);
        slot.TextureUploadIngress.Populate(
            textureUploadOperations,
            authoringTextureUploadOperationCount);
        // This is the sole object-to-stream boundary. Everything below,
        // including output ordering and resource-DAG compilation, consumes
        // numeric headers and dense typed payload streams only.
        slot.Operations.Lower(slot.StaticIngress);
        // Stable prepared cohorts are appended after ordinary operations in
        // exact cohort order, without renting MeshDrawOp instances.
        // Callers may supply null for legacy/cache-miss planning.
        if (preparedMeshIngress is not null)
            slot.Operations.AppendPreparedMeshIngress(
                preparedMeshIngress,
                dynamicUi: false);
        slot.DynamicOverlayOperations.Lower(slot.DynamicIngress);
        if (preparedMeshIngress is not null)
            slot.DynamicOverlayOperations.AppendPreparedMeshIngress(
                preparedMeshIngress,
                dynamicUi: true);
        slot.TextureUploadOperations.Lower(slot.TextureUploadIngress);
        // Ordering is deliberately inside the sealed numeric boundary. No DAG,
        // metadata, worker, or recording stage can observe an authoring FrameOp.
        VulkanCompiledRenderGraph graph = renderGraphAuthority.FallbackPlan.CompiledGraph;
        _frameScheduler.SortLoweredOperations(slot.Operations, graph);
        _frameScheduler.SortLoweredOperations(slot.DynamicOverlayOperations, graph);
        int outputCount = 0;
        int operationKeyCount = 0;
        AddPlanMetadata(slot, slot.Operations, dynamicOverlay: false, openXrViewKind, ref outputCount, ref operationKeyCount);
        AddPlanMetadata(slot, slot.DynamicOverlayOperations, dynamicOverlay: true, openXrViewKind, ref outputCount, ref operationKeyCount);
        bool requiresFreshEmptyTerminalWrite = false;
        if (emptyPresentNowOutputContract is { } emptyContract)
        {
            if (!emptyContract.IsDefined ||
                emptyContract.WorkClass != ERenderOutputWorkClass.PresentNow)
            {
                throw new ArgumentException(
                    "An empty foreground plan requires a defined PresentNow output contract.",
                    nameof(emptyPresentNowOutputContract));
            }

            int requiredOutputIndex = FindSchedulingContractOutputIndex(
                    slot,
                    outputCount,
                    in emptyContract);
            if (requiredOutputIndex < 0)
            {
                AddOutput(
                    slot,
                    OutputRequest.FromSchedulingRequest(in emptyContract),
                    ref outputCount);
                requiresFreshEmptyTerminalWrite = true;
            }
            else
            {
                slot.Outputs[requiredOutputIndex] =
                    slot.Outputs[requiredOutputIndex]
                        .WithSchedulingContract(in emptyContract);
            }
        }
        SortOutputs(slot.Outputs, outputCount);
        int outputExecutionNodeCount = CompileOutputGraph(
            slot,
            outputCount,
            renderFrameId,
            openXrImagesAcquired);
        if (emptyPresentNowOutputContract is { } requiredContract)
        {
            int requiredOutputIndex = FindSchedulingContractOutputIndex(
                slot,
                outputCount,
                in requiredContract);
            RenderOutputRequest resolvedContract = requiredOutputIndex < 0
                ? default
                : slot.OutputGraphRequests[requiredOutputIndex];
            if (requiredOutputIndex < 0 ||
                !slot.OutputExecutable[requiredOutputIndex] ||
                resolvedContract.WorkClass != requiredContract.WorkClass ||
                resolvedContract.ReadinessPolicy !=
                    requiredContract.ReadinessPolicy)
            {
                throw new InvalidOperationException(
                    "A required PresentNow output contract has no exact executable output-DAG terminal.");
            }
        }
        ApplyOutputAdmission(
            slot,
            slot.Operations,
            outputCount,
            openXrViewKind);
        ApplyOutputAdmission(
            slot,
            slot.DynamicOverlayOperations,
            outputCount,
            openXrViewKind);
        if (!HasAdmittedOutput(slot, outputCount))
            slot.TextureUploadOperations.Reset();
        int textureUploadExecutionNodeIndex = ResolveTextureUploadExecutionNodeIndex(
            slot.OutputExecutionNodes,
            outputExecutionNodeCount,
            slot.TextureUploadOperations.Count);
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
        // Pipeline manifests and command artifacts address operations by their
        // final sealed index. Hash only after output admission and dependency
        // ordering so two streams cannot share a warm manifest whose indexed
        // requirements belong to a different operation order.
        staticOperationSignature = VulkanFrameOperationSemantics.ComputeFrameOpsSignature(
            new FrameOperationSequence(slot.Operations));
        dynamicOverlaySignature = VulkanFrameOperationSemantics.ComputeFrameOpsSignature(
            new FrameOperationSequence(slot.DynamicOverlayOperations));
        operationKeyCount = RebuildOperationKeys(
            slot,
            operationCount,
            dynamicOperationCount);
        int staticPlannerContextKeyCount = CollectPlannerContextKeys(slot);
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
            slot.OutputGraphRequests,
            slot.OutputDecisions,
            outputCount,
            slot.OutputExecutionNodes,
            outputExecutionNodeCount,
            textureUploadExecutionNodeIndex,
            slot.OperationKeys,
            operationKeyCount,
            slot.StaticPlannerContextKeys,
            slot.StaticPlannerContexts,
            slot.StaticPlannerContextPlans,
            staticPlannerContextKeyCount,
            renderGraphPlanSignature,
            requiresFreshEmptyTerminalWrite,
            preparedMeshIngress?.StableBinStream);
        return slot.Plan;
    }

    private static int ResolveTextureUploadExecutionNodeIndex(
        RenderOutputDagNodeDescriptor[] nodes,
        int nodeCount,
        int uploadOperationCount)
    {
        for (int index = 0; index < nodeCount; index++)
            if (nodes[index].Kind == ERenderOutputDagNodeKind.Upload)
                return index;

        if (uploadOperationCount != 0)
            throw new InvalidOperationException(
                "The frame contains texture-upload operations without an executable output-DAG upload node.");
        return -1;
    }

    /// <summary>
    /// Captures every distinct resource-planner context consumed by either
    /// sealed recording stream. Dynamic overlay compute work is recorded after
    /// the static stream but still requires the same frozen physical generation
    /// guarantee during descriptor preparation.
    /// </summary>
    private static int CollectPlannerContextKeys(Slot slot)
    {
        int keyCount = CollectPlannerContextKeys(slot, slot.Operations, 0);
        return CollectPlannerContextKeys(slot, slot.DynamicOverlayOperations, keyCount);
    }

    private static int CollectPlannerContextKeys(
        Slot slot,
        FrameOperationStream operations,
        int keyCount)
    {
        for (int operationIndex = 0; operationIndex < operations.Count; operationIndex++)
        {
            ref readonly FrameOpContext context = ref operations.GetContext(operationIndex);
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

            RequireCapacity(
                slot.StaticPlannerContextKeys,
                keyCount + 1,
                EVulkanAcceptedFrameLane.PlannerContext);

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
        RequireCapacity(
            slot.StaticPlannerContextPlans,
            keyCount,
            EVulkanAcceptedFrameLane.PlannerContext);

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
                    $"resourceGeneration={key.ResourceGeneration}. " +
                    DescribeMissingPlannerPublication(slot, in key, in authority));
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

    private static string DescribeMissingPlannerPublication(
        Slot slot,
        in VulkanFrameOpPlannerStateKey missingKey,
        in VulkanFramePlanRenderGraphAuthority authority)
    {
        List<string> operations = new(8);
        for (int index = 0; index < slot.Operations.Count && operations.Count < 8; index++)
        {
            VulkanFrameOpPlannerStateKey operationKey =
                VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(
                    slot.Operations.GetContext(index));
            if (operationKey.Equals(missingKey))
                operations.Add($"{index}:{slot.Operations.GetHeader(index).OpCode}");
        }

        List<string> publications = new(8);
        if (authority.SwitchingState is { } switchingState)
        {
            foreach (VulkanFrameOpPlannerStateKey key in switchingState.States.Keys)
            {
                if (publications.Count == 8)
                    break;
                publications.Add(DescribePlannerKey(in key));
            }
        }

        return $"Missing={DescribePlannerKey(in missingKey)} " +
            $"Operations=[{string.Join(',', operations)}] " +
            $"Publications=[{string.Join(',', publications)}].";
    }

    private static string DescribePlannerKey(in VulkanFrameOpPlannerStateKey key)
        => $"{key.ContextKind}/p{key.PipelineIdentity}/v{key.ViewportIdentity}" +
           $"/d{key.DisplayWidth}x{key.DisplayHeight}/i{key.InternalWidth}x{key.InternalHeight}" +
           $"/fbo{key.OutputFrameBufferIdentity}/target{key.OutputTargetIdentity}" +
           $"/view{key.LogicalViewId:X16}/registry{key.ResourceRegistrySignature}" +
           $"/passes{key.PassMetadataSignature}/g{key.ResourceGeneration}" +
           $"/descriptors{key.DescriptorGeneration}/queue{key.SubmissionQueueFamily}";

    private Slot AcquireWritableSlot(int frameSlot)
    {
        if ((uint)frameSlot >= (uint)_slots.Length)
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.FrameSlot,
                _slots.Length,
                frameSlot + 1);
        Slot active = _slots[frameSlot];
        if (!active.Plan.IsPinned)
            return active;

        Slot replacement = TakeUnpinnedRetiredSlot() ??
            throw new VulkanAcceptedFramePlanCapacityException(
                EVulkanAcceptedFrameLane.FrameSlot,
                _slots.Length + _retiredSlots.Length,
                _slots.Length + _retiredSlots.Length + 1);
        _slots[frameSlot] = replacement;
        RetireSlot(active);
        return replacement;
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
        RequireCapacity(
            _retiredSlots,
            _retiredSlotCount + 1,
            EVulkanAcceptedFrameLane.FrameSlot);
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
        RequireCapacity(
            orderScratch,
            source.Count,
            dynamicOverlay
                ? EVulkanAcceptedFrameLane.Ui
                : EVulkanAcceptedFrameLane.MainScene);
        for (int index = 0; index < source.Count; index++)
            orderScratch[index] = index;
        SortOperationOrder(
            slot,
            source,
            orderScratch,
            source.Count,
            outputCount,
            openXrViewKind);
        RequireCapacity(
            slot.OperationDependencyScratch,
            source.Count,
            EVulkanAcceptedFrameLane.Dependency);
        RequireCapacity(
            slot.OperationTopologicalOrderScratch,
            source.Count,
            EVulkanAcceptedFrameLane.Dependency);
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
        RequireCapacity(
            slot.OperationPriorityScratch,
            operationCount,
            EVulkanAcceptedFrameLane.Dependency);
        RequireCapacity(
            slot.OperationReadyHeapScratch,
            operationCount,
            EVulkanAcceptedFrameLane.Dependency);
        RequireCapacity(
            slot.ResourceProducerDependencyScratch,
            operationCount,
            EVulkanAcceptedFrameLane.Dependency);
        RequireCapacity(
            slot.DependencyFirstEdgeScratch,
            operationCount,
            EVulkanAcceptedFrameLane.Dependency);
        int[] priorities = slot.OperationPriorityScratch;
        int[] readyHeap = slot.OperationReadyHeapScratch;
        int[] firstEdges = slot.DependencyFirstEdgeScratch;
        Array.Fill(firstEdges, -1, 0, operationCount);
        slot.LastResourceWriters.Clear();
        int edgeCount = 0;
        Span<int> dependencies = slot.ResourceProducerDependencyScratch.AsSpan(
            0,
            operationCount);

        for (int index = 0; index < operationCount; index++)
        {
            indegree[index] = 0;
            ReadOnlySpan<FrameOpResourceUse> uses =
                operations.GetResourceUses(index);
            int dependencyCount = 0;
            for (int useIndex = 0; useIndex < uses.Length; useIndex++)
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
            for (int useIndex = 0; useIndex < uses.Length; useIndex++)
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
        RequireCapacity(
            slot.DependencyEdgeConsumerScratch,
            edgeCount + 1,
            EVulkanAcceptedFrameLane.Dependency);
        RequireCapacity(
            slot.DependencyEdgeNextScratch,
            edgeCount + 1,
            EVulkanAcceptedFrameLane.Dependency);
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
        EVrOutputViewKind? operationViewKind = ResolveOperationViewKind(
            slot,
            context,
            openXrViewKind);
        OutputRequest request = OutputRequest.FromContext(
            context,
            operationViewKind);
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
        RequireCapacity(
            slot.OperationKeys,
            operationCount + dynamicOperationCount,
            EVulkanAcceptedFrameLane.Dependency);
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
            EVrOutputViewKind? operationViewKind = ResolveOperationViewKind(
                slot,
                context,
                openXrViewKind);
            AddOutput(slot, OutputRequest.FromContext(context, operationViewKind), ref outputCount);
            RequireCapacity(
                slot.OperationKeys,
                operationKeyCount + 1,
                EVulkanAcceptedFrameLane.Dependency);
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

        RequireCapacity(
            slot.Outputs,
            outputCount + 1,
            EVulkanAcceptedFrameLane.Output);
        slot.Outputs[outputCount++] = request;
    }

    private static int FindSchedulingContractOutputIndex(
        Slot slot,
        int outputCount,
        in RenderOutputRequest contract)
    {
        for (int index = 0; index < outputCount; index++)
            if (slot.Outputs[index].MatchesSchedulingContract(in contract))
                return index;

        return -1;
    }

    private int CompileOutputGraph(
        Slot slot,
        int outputCount,
        ulong renderFrameId,
        bool openXrImagesAcquired)
    {
        if (outputCount == 0)
            return 0;

        _outputGraphPlanner.BeginManifest(renderFrameId);
        RequireCapacity(
            slot.OutputGraphRequests,
            outputCount,
            EVulkanAcceptedFrameLane.Output);
        RequireCapacity(
            slot.OutputDecisions,
            outputCount,
            EVulkanAcceptedFrameLane.Output);
        RequireCapacity(
            slot.OutputDue,
            outputCount,
            EVulkanAcceptedFrameLane.Output);
        for (int index = 0; index < outputCount; index++)
        {
            ref readonly OutputRequest output = ref slot.Outputs[index];
            RenderOutputRequest request = output.ToGraphRequest(
                renderFrameId,
                out RenderOutputSchedulingDecision decision);
            bool reserveXrPath = openXrImagesAcquired &&
                output.OutputKind == EFrameOutputKind.OpenXREyeSubmit;
            decision = WithActualXrReservation(decision, reserveXrPath);
            slot.OutputGraphRequests[index] = request;
            slot.OutputDecisions[index] = decision;
            slot.OutputDue[index] = decision.Execute;
            if (!_outputGraphPlanner.Reserve(request))
            {
                throw new InvalidOperationException(
                    "Frame output planning exceeded the configured DAG reservation capacity or used an invalid frame id.");
            }
        }

        for (int index = 0; index < outputCount; index++)
        {
            ref readonly OutputRequest output = ref slot.Outputs[index];
            RenderOutputRequest request = slot.OutputGraphRequests[index];
            bool independentDesktopScene = output.OutputKind != EFrameOutputKind.DesktopMirror;
            int terminalNode = _outputGraphPlanner.Plan(
                request,
                slot.OutputDecisions[index],
                independentDesktopScene,
                EFrameOutputKind.OpenXREyeSubmit,
                xrImagesAcquired: openXrImagesAcquired &&
                    output.OutputKind == EFrameOutputKind.OpenXREyeSubmit);
            if (terminalNode < 0)
            {
                throw new InvalidOperationException(
                    "Frame output planning exceeded the configured DAG capacity or used an invalid frame id.");
            }
        }

        AddExplicitOutputDependencies(slot, outputCount);
        // Explicit producer edges are known only after all terminals exist.
        // Re-propagate scheduling now so an acquired XR terminal promotes its
        // entire final prerequisite path, including late dataflow edges.
        for (int index = 0; index < outputCount; index++)
        {
            ref readonly OutputRequest output = ref slot.Outputs[index];
            _ = _outputGraphPlanner.RefreshSchedule(
                slot.OutputGraphRequests[index],
                openXrImagesAcquired &&
                    output.OutputKind == EFrameOutputKind.OpenXREyeSubmit);
        }

        RenderOutputDag graph = _outputGraphPlanner.Graph;
        RequireCapacity(
            slot.OutputExecutionNodes,
            graph.NodeCount,
            EVulkanAcceptedFrameLane.Output);
        RequireCapacity(
            slot.OutputNodeOrderScratch,
            graph.NodeCount,
            EVulkanAcceptedFrameLane.Output);
        RequireCapacity(
            slot.OutputNodeIndegreeScratch,
            graph.SlotCount,
            EVulkanAcceptedFrameLane.Output);
        if (!graph.TryCompileDeadlineOrder(
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

        RequireCapacity(
            slot.OutputExecutionRanks,
            outputCount,
            EVulkanAcceptedFrameLane.Output);
        for (int outputIndex = 0; outputIndex < outputCount; outputIndex++)
        {
            RenderOutputRequest request = slot.OutputGraphRequests[outputIndex];
            if (!_outputGraphPlanner.TryGetTerminalNodeIndex(
                    request,
                    out int terminalNodeIndex))
            {
                throw new InvalidOperationException(
                    "A reserved output request has no terminal DAG node.");
            }

            bool executable = graph.IsExecutable(terminalNodeIndex);
            slot.OutputExecutable[outputIndex] = executable;
            ulong terminalNodeKey = graph.GetNode(terminalNodeIndex).StableNodeKey;
            int rank = int.MaxValue;
            for (int nodeIndex = 0; nodeIndex < executionNodeCount; nodeIndex++)
            {
                if (slot.OutputExecutionNodes[nodeIndex].StableNodeKey == terminalNodeKey)
                {
                    rank = nodeIndex;
                    break;
                }
            }
            if (rank == int.MaxValue)
            {
                if (executable || slot.OutputDecisions[outputIndex].Execute)
                {
                    throw new InvalidOperationException(
                        "An admitted output request has no executable DAG terminal.");
                }
            }
            else if (!slot.OutputDecisions[outputIndex].Execute)
            {
                RenderOutputSchedulingDecision decision =
                    slot.OutputDecisions[outputIndex];
                slot.OutputDecisions[outputIndex] = new(
                    Execute: true,
                    ERenderOutputWorkDisposition.FreshRender,
                    ERenderOutputPolicyReason.DependencyRequired,
                    decision.ContentAgeFrames,
                    decision.XrCriticalPathReserved,
                    ForcedRefresh: true);
            }

            slot.OutputExecutionRanks[outputIndex] = rank;
        }

        return executionNodeCount;
    }

    private static RenderOutputSchedulingDecision WithActualXrReservation(
        in RenderOutputSchedulingDecision decision,
        bool reserveXrPath)
    {
        ERenderOutputPolicyReason reason = decision.Reason;
        if (decision.Execute &&
            decision.Disposition == ERenderOutputWorkDisposition.FreshRender)
        {
            if (reserveXrPath && reason == ERenderOutputPolicyReason.None)
                reason = ERenderOutputPolicyReason.XrCriticalPathReserved;
            else if (!reserveXrPath && reason == ERenderOutputPolicyReason.XrCriticalPathReserved)
                reason = ERenderOutputPolicyReason.None;
        }

        return decision with
        {
            Reason = reason,
            XrCriticalPathReserved = reserveXrPath,
        };
    }

    private static void ApplyOutputAdmission(
        Slot slot,
        FrameOperationStream operations,
        int outputCount,
        EVrOutputViewKind? openXrViewKind)
    {
        if (operations.Count == 0)
            return;

        RequireCapacity(
            slot.OperationOrderScratch,
            operations.Count,
            EVulkanAcceptedFrameLane.MainScene);
        int retainedCount = 0;
        for (int operationIndex = 0; operationIndex < operations.Count; operationIndex++)
        {
            ref readonly FrameOpContext context = ref operations.GetContext(operationIndex);
            EVrOutputViewKind? operationViewKind = ResolveOperationViewKind(
                slot,
                context,
                openXrViewKind);
            OutputRequest operationOutput = OutputRequest.FromContext(
                context,
                operationViewKind);
            for (int outputIndex = 0; outputIndex < outputCount; outputIndex++)
            {
                if (!slot.Outputs[outputIndex].MatchesOutput(operationOutput))
                    continue;
                if (slot.OutputExecutable[outputIndex])
                    slot.OperationOrderScratch[retainedCount++] = operationIndex;
                break;
            }
        }

        if (retainedCount != operations.Count)
            operations.Retain(slot.OperationOrderScratch.AsSpan(0, retainedCount));
    }

    private static EVrOutputViewKind? ResolveOperationViewKind(
        Slot slot,
        in FrameOpContext context,
        EVrOutputViewKind? openXrViewKind)
    {
        if (context.ContextKind == EVulkanFrameOpContextKind.OpenXrEye &&
            slot.ViewSet.TryGetLocatedOpenXrViewKindByLogicalViewId(
                context.LogicalViewId,
                out EVrOutputViewKind locatedViewKind))
        {
            return locatedViewKind;
        }

        return openXrViewKind;
    }

    private static bool HasAdmittedOutput(Slot slot, int outputCount)
    {
        for (int index = 0; index < outputCount; index++)
            if (slot.OutputExecutable[index])
                return true;
        return false;
    }

    private void AddExplicitOutputDependencies(
        Slot slot,
        int outputCount)
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
                    slot.OutputGraphRequests[producerIndex],
                    slot.OutputGraphRequests[dependentIndex],
                    out string? failureReason))
            {
                throw new InvalidOperationException(
                    $"Frame output dataflow dependency is invalid: {failureReason}.");
            }
        }
    }

    private static void TryAttachLocatedOpenXrViews(ViewSetPlan viewSet)
    {
        if (RenderFrameViewSetPublication.TryGetLatest(out RenderFrameViewSet locatedViews))
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

    private static void RequireCapacity<T>(
        T[] storage,
        int required,
        EVulkanAcceptedFrameLane lane)
    {
        if (storage.Length >= required)
            return;

        throw new VulkanAcceptedFramePlanCapacityException(
            lane,
            storage.Length,
            required);
    }

}

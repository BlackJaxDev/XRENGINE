namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Sealed frame-slot-owned lowering of the current static and dynamic frame-op
/// streams. Its arrays are owned by one <see cref="FramePlanBuilder"/> slot and
/// remain immutable until that same slot begins a later frame.
/// </summary>
internal sealed class FramePlan
{
    private FrameOperationStream _operations = new();
    private FrameOperationStream _dynamicOverlayOperations = new();
    private FrameOperationStream _textureUploadOperations = new();
    private OutputRequest[] _outputs = Array.Empty<OutputRequest>();
    private RenderOutputRequest[] _outputRequests = Array.Empty<RenderOutputRequest>();
    private RenderOutputSchedulingDecision[] _outputDecisions =
        Array.Empty<RenderOutputSchedulingDecision>();
    private RenderOutputDagNodeDescriptor[] _outputExecutionNodes =
        Array.Empty<RenderOutputDagNodeDescriptor>();
    private FramePlanOperationKey[] _operationKeys = Array.Empty<FramePlanOperationKey>();
    private VulkanFrameOpPlannerStateKey[] _staticPlannerContextKeys =
        Array.Empty<VulkanFrameOpPlannerStateKey>();
    private FrameOpContext[] _staticPlannerContexts = Array.Empty<FrameOpContext>();
    private VulkanRenderGraphPlan[] _staticPlannerContextPlans =
        Array.Empty<VulkanRenderGraphPlan>();
    private int _operationCount;
    private int _dynamicOverlayOperationCount;
    private int _textureUploadOperationCount;
    private int _outputCount;
    private int _outputExecutionNodeCount;
    private int _operationKeyCount;
    private int _staticPlannerContextKeyCount;
    private int _leaseCount;
    private readonly object _leaseGate = new();

    internal int FrameSlot { get; private set; } = -1;
    internal ulong Generation { get; private set; }
    internal ulong RenderFrameId { get; private set; }
    internal ulong PlannerRevision { get; private set; }
    internal ulong ResourceVersionSignature { get; private set; }
    internal ulong DescriptorVersionSignature { get; private set; }
    internal ulong StaticOperationSignature { get; private set; }
    internal ulong DynamicOverlaySignature { get; private set; }
    internal ulong RenderGraphPlanSignature { get; private set; }
    internal ViewSetPlan ViewSet { get; }
    internal bool IsSealed { get; private set; }
    internal int OperationCount => _operationCount;
    internal int DynamicOverlayOperationCount => _dynamicOverlayOperationCount;
    internal int TextureUploadOperationCount => _textureUploadOperationCount;
    internal int TextureUploadExecutionNodeIndex { get; private set; } = -1;
    /// <summary>Canonical numeric static stream for planners and schedulers.</summary>
    internal FrameOperationStream StaticOperations => _operations;
    /// <summary>Canonical numeric dynamic-overlay stream for planners and schedulers.</summary>
    internal FrameOperationStream DynamicOverlayOperations => _dynamicOverlayOperations;
    /// <summary>Canonical numeric transfer stream recorded before the primary stream.</summary>
    internal FrameOperationStream TextureUploadOperations => _textureUploadOperations;
    internal int OutputCount => _outputCount;
    /// <summary>
    /// Number of output/resource DAG nodes in the validated deterministic order
    /// consumed before native command recording.
    /// </summary>
    internal int OutputExecutionNodeCount => _outputExecutionNodeCount;
    internal int OperationKeyCount => _operationKeyCount;
    internal ReadOnlySpan<VulkanFrameOpPlannerStateKey> StaticPlannerContextKeys
        => _staticPlannerContextKeys.AsSpan(0, _staticPlannerContextKeyCount);
    internal ReadOnlySpan<VulkanRenderGraphPlan> StaticPlannerContextPlans
        => _staticPlannerContextPlans.AsSpan(0, _staticPlannerContextKeyCount);
    internal bool IsPinned
    {
        get
        {
            lock (_leaseGate)
                return _leaseCount != 0;
        }
    }
    /// <summary>
    /// Returns the sealed numeric stream directly to native recording. The
    /// stream exposes indexed reads only, so the recorder cannot alter the
    /// plan-owned order or materialize a per-frame compatibility array.
    /// </summary>
    internal FrameOperationSequence GetNativeStaticOperationsForRecording()
    {
        EnsureSealed();
        return new FrameOperationSequence(_operations);
    }

    internal FrameOperationSequence GetNativeDynamicOverlayOperationsForRecording()
    {
        EnsureSealed();
        return new FrameOperationSequence(_dynamicOverlayOperations);
    }

    internal FrameOperationSequence GetNativeTextureUploadOperationsForRecording()
    {
        EnsureSealed();
        return new FrameOperationSequence(_textureUploadOperations);
    }

    /// <summary>
    /// Returns a header-only logical-view slice over the plan's already sealed
    /// payload store. No eye authoring operation is inspected or lowered here.
    /// </summary>
    internal FrameOperationSequence GetNativeStaticOperationsForLogicalView(
        ulong logicalViewId)
    {
        EnsureSealed();
        if (logicalViewId == 0UL)
            throw new ArgumentOutOfRangeException(nameof(logicalViewId));
        FrameOperationStream slice = _operations.CreateLogicalViewSlice(logicalViewId);
        if (slice.Count == 0)
            throw new InvalidOperationException("The sealed frame plan has no operations for the requested logical view.");
        return new FrameOperationSequence(slice);
    }

    internal FramePlan(ViewSetPlan viewSet)
        => ViewSet = viewSet;

    internal void Publish(
        int frameSlot,
        ulong generation,
        ulong renderFrameId,
        ulong plannerRevision,
        ulong resourceVersionSignature,
        ulong descriptorVersionSignature,
        ulong staticOperationSignature,
        ulong dynamicOverlaySignature,
        FrameOperationStream operations,
        FrameOperationStream dynamicOverlayOperations,
        FrameOperationStream textureUploadOperations,
        OutputRequest[] outputs,
        RenderOutputRequest[] outputRequests,
        RenderOutputSchedulingDecision[] outputDecisions,
        int outputCount,
        RenderOutputDagNodeDescriptor[] outputExecutionNodes,
        int outputExecutionNodeCount,
        int textureUploadExecutionNodeIndex,
        FramePlanOperationKey[] operationKeys,
        int operationKeyCount,
        VulkanFrameOpPlannerStateKey[] staticPlannerContextKeys,
        FrameOpContext[] staticPlannerContexts,
        VulkanRenderGraphPlan[] staticPlannerContextPlans,
        int staticPlannerContextKeyCount,
        ulong renderGraphPlanSignature)
    {
        lock (_leaseGate)
        {
            if (IsSealed || _leaseCount != 0)
                throw new InvalidOperationException("A sealed frame plan must be reset by its owning frame slot.");

            FrameSlot = frameSlot;
            Generation = generation;
            RenderFrameId = renderFrameId;
            PlannerRevision = plannerRevision;
            ResourceVersionSignature = resourceVersionSignature;
            DescriptorVersionSignature = descriptorVersionSignature;
            StaticOperationSignature = staticOperationSignature;
            DynamicOverlaySignature = dynamicOverlaySignature;
            RenderGraphPlanSignature = renderGraphPlanSignature;
            _operations = operations;
            _dynamicOverlayOperations = dynamicOverlayOperations;
            _textureUploadOperations = textureUploadOperations;
            _operationCount = operations.Count;
            _dynamicOverlayOperationCount = dynamicOverlayOperations.Count;
            _textureUploadOperationCount = textureUploadOperations.Count;
            _outputs = outputs;
            _outputRequests = outputRequests;
            _outputDecisions = outputDecisions;
            _outputCount = outputCount;
            _outputExecutionNodes = outputExecutionNodes;
            _outputExecutionNodeCount = outputExecutionNodeCount;
            TextureUploadExecutionNodeIndex = textureUploadExecutionNodeIndex;
            _operationKeys = operationKeys;
            _operationKeyCount = operationKeyCount;
            _staticPlannerContextKeys = staticPlannerContextKeys;
            _staticPlannerContexts = staticPlannerContexts;
            _staticPlannerContextPlans = staticPlannerContextPlans;
            _staticPlannerContextKeyCount = staticPlannerContextKeyCount;
            ViewSet.Seal();
            IsSealed = true;
        }
    }

    internal void Reset()
    {
        lock (_leaseGate)
        {
            if (_leaseCount != 0)
                throw new InvalidOperationException("A pinned frame plan cannot be reset.");

            FrameSlot = -1;
            TextureUploadExecutionNodeIndex = -1;
            Generation = 0;
            RenderFrameId = 0;
            PlannerRevision = 0;
            ResourceVersionSignature = 0;
            DescriptorVersionSignature = 0;
            StaticOperationSignature = 0;
            DynamicOverlaySignature = 0;
            RenderGraphPlanSignature = 0;
            _operationCount = 0;
            _dynamicOverlayOperationCount = 0;
            _textureUploadOperationCount = 0;
            _outputCount = 0;
            _outputExecutionNodeCount = 0;
            _operationKeyCount = 0;
            Array.Clear(_staticPlannerContexts, 0, _staticPlannerContextKeyCount);
            Array.Clear(_staticPlannerContextPlans, 0, _staticPlannerContextKeyCount);
            _staticPlannerContextKeyCount = 0;
            IsSealed = false;
            ViewSet.Reset();
        }
    }

    /// <summary>
    /// Pins this publication while an asynchronous prepared-frame consumer owns
    /// indices into its slot storage. A builder must publish a different slot
    /// rather than reset a pinned plan.
    /// </summary>
    internal void AcquireLease()
    {
        lock (_leaseGate)
        {
            EnsureSealed();
            _leaseCount++;
        }
    }

    internal void ReleaseLease()
    {
        lock (_leaseGate)
        {
            if (_leaseCount <= 0)
                throw new InvalidOperationException("Frame-plan lease underflow.");
            if (_leaseCount > 1)
            {
                _leaseCount--;
                return;
            }

            _leaseCount = 0;
        }
    }

    internal bool MatchesPublication(
        ulong renderFrameId,
        ulong plannerRevision,
        ulong staticOperationSignature,
        ulong dynamicOverlaySignature)
        => IsSealed &&
           RenderFrameId == renderFrameId &&
           PlannerRevision == plannerRevision &&
           StaticOperationSignature == staticOperationSignature &&
           DynamicOverlaySignature == dynamicOverlaySignature;

    /// <summary>
    /// Verifies that native recording consumes the sealed static stream owned by
    /// this publication, rather than a producer-owned or subsequently rebuilt
    /// operation array.
    /// </summary>
    internal bool TryValidateNativeRecording(FrameOperationSequence operations, out string reason)
    {
        if (!IsSealed)
        {
            reason = "frame plan is not sealed";
            return false;
        }

        if (_operationCount == operations.Length)
        {
            for (int index = 0; index < _operationCount; index++)
                if (_operations.GetHeader(index).OpCode != operations.GetHeader(index).OpCode ||
                    _operations.GetContext(index).RecordingFingerprint != operations.GetContext(index).RecordingFingerprint)
                {
                    reason = "operation stream does not match the immutable frame-plan snapshot";
                    return false;
                }
        }
        else if (!MatchesSingleLogicalViewSnapshot(operations))
        {
            reason = "frame plan operation count does not match the native recording stream";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool MatchesSingleLogicalViewSnapshot(FrameOperationSequence operations)
    {
        if (operations.Length == 0)
            return false;

        ulong logicalViewId = operations.GetContext(0).LogicalViewId;
        if (logicalViewId == 0UL)
            return false;

        int nativeIndex = 0;
        for (int planIndex = 0; planIndex < _operationCount; planIndex++)
        {
            ref readonly FrameOperationHeader planHeader = ref _operations.GetHeader(planIndex);
            if (_operations.GetContext(planIndex).LogicalViewId != logicalViewId)
                continue;
            if (nativeIndex >= operations.Length ||
                planHeader.OpCode != operations.GetHeader(nativeIndex).OpCode ||
                operations.GetContext(nativeIndex).LogicalViewId != logicalViewId)
                return false;
            nativeIndex++;
        }

        return nativeIndex == operations.Length;
    }

    /// <summary>
    /// Resolves the immutable graph publication owned by the supplied operation
    /// context. The bounded linear lookup avoids a dictionary allocation in the
    /// recording hot path and runs only when the active context changes.
    /// </summary>
    internal bool TryResolveRenderGraphPlan(
        in FrameOpContext context,
        out VulkanRenderGraphPlan plan)
    {
        EnsureSealed();
        if (context.ResourceRegistry is null && context.PassMetadata is not { Count: > 0 })
        {
            plan = VulkanRenderGraphPlan.Empty;
            return false;
        }

        for (int index = 0; index < _staticPlannerContextKeyCount; index++)
        {
            if (!VulkanFrameOpSnapshotSignatures.MatchesPlannerStateKey(
                    in context,
                    in _staticPlannerContextKeys[index],
                    _staticPlannerContexts[index].PassMetadata))
                continue;

            plan = _staticPlannerContextPlans[index];
            return true;
        }

        plan = VulkanRenderGraphPlan.Empty;
        return false;
    }

    internal ref readonly OutputRequest GetOutput(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)_outputCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _outputs[index];
    }

    internal ref readonly RenderOutputRequest GetOutputRequest(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)_outputCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _outputRequests[index];
    }

    /// <summary>
    /// Resolves the strictest executable foreground contract without allocating
    /// or depending on output declaration order.
    /// </summary>
    internal bool TryGetPresentNowContract(out RenderOutputRequest request)
    {
        EnsureSealed();
        int selected = -1;
        for (int index = 0; index < _outputCount; index++)
        {
            ref readonly RenderOutputRequest candidate =
                ref _outputRequests[index];
            if (!_outputDecisions[index].Execute ||
                candidate.WorkClass != ERenderOutputWorkClass.PresentNow)
            {
                continue;
            }

            if (selected < 0 ||
                IsStricterReadiness(
                    candidate.ReadinessPolicy,
                    _outputRequests[selected].ReadinessPolicy))
            {
                selected = index;
            }
        }

        if (selected < 0)
        {
            request = default;
            return false;
        }

        request = _outputRequests[selected];
        return true;
    }

    private static bool IsStricterReadiness(
        ERenderOutputReadinessPolicy candidate,
        ERenderOutputReadinessPolicy current)
        => candidate switch
        {
            ERenderOutputReadinessPolicy.BlockForExact =>
                current != ERenderOutputReadinessPolicy.BlockForExact,
            ERenderOutputReadinessPolicy.MeetDeadlineWithGpuFallback =>
                current == ERenderOutputReadinessPolicy.AllowDeferral,
            _ => false,
        };

    /// <summary>
    /// Returns whether the immutable output manifest admitted native execution
    /// for at least one output of the requested kind.
    /// </summary>
    internal bool HasExecutableOutput(EFrameOutputKind kind)
    {
        EnsureSealed();
        for (int index = 0; index < _outputCount; index++)
        {
            if (_outputRequests[index].OutputKind != kind ||
                !_outputDecisions[index].Execute)
            {
                continue;
            }

            ulong outputId = _outputRequests[index].OutputId;
            for (int nodeIndex = 0; nodeIndex < _outputExecutionNodeCount; nodeIndex++)
            {
                if (_outputExecutionNodes[nodeIndex].StableOutputKey == outputId)
                    return true;
            }
        }
        return false;
    }

    internal bool HasAnyExecutableOutput
    {
        get
        {
            EnsureSealed();
            return _outputExecutionNodeCount != 0;
        }
    }

    internal ref readonly RenderOutputSchedulingDecision GetOutputDecision(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)_outputCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return ref _outputDecisions[index];
    }

    internal ref readonly RenderOutputDagNodeDescriptor GetOutputExecutionNode(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)_outputExecutionNodeCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _outputExecutionNodes[index];
    }

    /// <summary>
    /// Gets the stable logical key for an operation in this frame plan. Physical
    /// <see cref="RecordedPacketKey"/> instances are captured later, when native
    /// resources and descriptor state have been prepared.
    /// </summary>
    internal ref readonly FramePlanOperationKey GetOperationKey(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)_operationKeyCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _operationKeys[index];
    }

    private void EnsureSealed()
    {
        if (!IsSealed)
            throw new InvalidOperationException("The frame plan must be sealed before consumption.");
    }
}

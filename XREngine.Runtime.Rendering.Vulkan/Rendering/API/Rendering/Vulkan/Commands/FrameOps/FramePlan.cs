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
    private OutputRequest[] _outputs = Array.Empty<OutputRequest>();
    private RenderOutputDagNodeDescriptor[] _outputExecutionNodes =
        Array.Empty<RenderOutputDagNodeDescriptor>();
    private FramePlanOperationKey[] _operationKeys = Array.Empty<FramePlanOperationKey>();
    private VulkanFrameOpPlannerStateKey[] _staticPlannerContextKeys =
        Array.Empty<VulkanFrameOpPlannerStateKey>();
    private int _operationCount;
    private int _dynamicOverlayOperationCount;
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
    internal ViewSetPlan ViewSet { get; }
    internal bool IsSealed { get; private set; }
    internal int OperationCount => _operationCount;
    internal int DynamicOverlayOperationCount => _dynamicOverlayOperationCount;
    /// <summary>Canonical numeric static stream for planners and schedulers.</summary>
    internal FrameOperationStream StaticOperations => _operations;
    /// <summary>Canonical numeric dynamic-overlay stream for planners and schedulers.</summary>
    internal FrameOperationStream DynamicOverlayOperations => _dynamicOverlayOperations;
    internal int OutputCount => _outputCount;
    /// <summary>
    /// Number of output/resource DAG nodes in the validated deterministic order
    /// consumed before native command recording.
    /// </summary>
    internal int OutputExecutionNodeCount => _outputExecutionNodeCount;
    internal int OperationKeyCount => _operationKeyCount;
    internal ReadOnlySpan<VulkanFrameOpPlannerStateKey> StaticPlannerContextKeys
        => _staticPlannerContextKeys.AsSpan(0, _staticPlannerContextKeyCount);
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

    /// <summary>
    /// Validates the caller's already prepared native eye operations against a
    /// target-neutral logical slice, then exposes the prepared array through a
    /// read-only recording sequence. The caller already owns this per-eye
    /// capture, so cloning it again would add transient work without improving
    /// plan isolation.
    /// </summary>
    internal FrameOperationSequence GetNativeStaticOperationsForLogicalView(
        ulong logicalViewId,
        FrameOp[] nativeOperations)
    {
        EnsureSealed();
        ArgumentNullException.ThrowIfNull(nativeOperations);
        if (logicalViewId == 0UL || nativeOperations.Length == 0)
            throw new InvalidOperationException("A paired-eye frame plan requires a non-empty logical view slice.");

        int nativeIndex = 0;
        for (int planIndex = 0; planIndex < _operationCount; planIndex++)
        {
            FrameOp logicalOperation = _operations.GetPayloadForPrimaryDispatch(planIndex);
            if (logicalOperation.Context.LogicalViewId != logicalViewId)
                continue;
            if (nativeIndex >= nativeOperations.Length ||
                logicalOperation.Kind != nativeOperations[nativeIndex].Kind ||
                nativeOperations[nativeIndex].Context.LogicalViewId != logicalViewId)
            {
                throw new InvalidOperationException("Native eye operations do not match the shared logical plan slice.");
            }

            nativeIndex++;
        }

        if (nativeIndex != nativeOperations.Length)
            throw new InvalidOperationException("Native eye operation count does not match the shared logical plan slice.");
        return new FrameOperationSequence(nativeOperations);
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
        OutputRequest[] outputs,
        int outputCount,
        RenderOutputDagNodeDescriptor[] outputExecutionNodes,
        int outputExecutionNodeCount,
        FramePlanOperationKey[] operationKeys,
        int operationKeyCount,
        VulkanFrameOpPlannerStateKey[] staticPlannerContextKeys,
        int staticPlannerContextKeyCount)
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
            _operations = operations;
            _dynamicOverlayOperations = dynamicOverlayOperations;
            _operationCount = operations.Count;
            _dynamicOverlayOperationCount = dynamicOverlayOperations.Count;
            _outputs = outputs;
            _outputCount = outputCount;
            _outputExecutionNodes = outputExecutionNodes;
            _outputExecutionNodeCount = outputExecutionNodeCount;
            _operationKeys = operationKeys;
            _operationKeyCount = operationKeyCount;
            _staticPlannerContextKeys = staticPlannerContextKeys;
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
            Generation = 0;
            RenderFrameId = 0;
            PlannerRevision = 0;
            ResourceVersionSignature = 0;
            DescriptorVersionSignature = 0;
            StaticOperationSignature = 0;
            DynamicOverlaySignature = 0;
            _operationCount = 0;
            _dynamicOverlayOperationCount = 0;
            _outputCount = 0;
            _outputExecutionNodeCount = 0;
            _operationKeyCount = 0;
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
            if (_leaseCount++ != 0)
                return;

            LeaseOperations(_operations, _operationCount, acquire: true);
            LeaseOperations(
                _dynamicOverlayOperations,
                _dynamicOverlayOperationCount,
                acquire: true);
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

            // Keep the plan visibly pinned until all pooled operations have been
            // released. Reset cannot enter this gate and republish their slots
            // halfway through the final release loop.
            LeaseOperations(_operations, _operationCount, acquire: false);
            LeaseOperations(
                _dynamicOverlayOperations,
                _dynamicOverlayOperationCount,
                acquire: false);
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
                if (!ReferenceEquals(_operations.GetPayloadForPrimaryDispatch(index), operations[index]))
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

        ulong logicalViewId = operations[0].Context.LogicalViewId;
        if (logicalViewId == 0UL)
            return false;

        int nativeIndex = 0;
        for (int planIndex = 0; planIndex < _operationCount; planIndex++)
        {
            FrameOp planOperation = _operations.GetPayloadForPrimaryDispatch(planIndex);
            if (planOperation.Context.LogicalViewId != logicalViewId)
                continue;
            if (nativeIndex >= operations.Length ||
                planOperation.Kind != operations[nativeIndex].Kind ||
                operations[nativeIndex].Context.LogicalViewId != logicalViewId)
                return false;
            nativeIndex++;
        }

        return nativeIndex == operations.Length;
    }

    private static void LeaseOperations(FrameOperationStream operations, int count, bool acquire)
    {
        for (int index = 0; index < count; index++)
        {
            if (acquire)
                operations.GetPayloadForPrimaryDispatch(index).AcquireFramePlanLease();
            else
                operations.GetPayloadForPrimaryDispatch(index).ReleaseFramePlanLease();
        }
    }

    internal ref readonly FrameOp GetOperation(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)_operationCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _operations.GetPayloadForPrimaryDispatch(index);
    }

    internal ref readonly FrameOp GetDynamicOverlayOperation(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)_dynamicOverlayOperationCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _dynamicOverlayOperations.GetPayloadForPrimaryDispatch(index);
    }

    internal ref readonly OutputRequest GetOutput(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)_outputCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _outputs[index];
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

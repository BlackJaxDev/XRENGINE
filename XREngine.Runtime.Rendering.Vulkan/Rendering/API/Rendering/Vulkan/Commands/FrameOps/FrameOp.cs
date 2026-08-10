using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal abstract record FrameOp(int PassIndex, XRFrameBuffer? Target, FrameOpContext Context)
{
    private int _framePlanLeaseCount;
    private bool _isSealedForFramePlan;
    private int _passIndex = PassIndex;
    private XRFrameBuffer? _target = Target;
    private FrameOpContext _context = Context;
    private FrameOpResourceUseList _resourceUses;

    public int PassIndex
    {
        get => _passIndex;
        internal set
        {
            ThrowIfSealedForFramePlan();
            _passIndex = value;
        }
    }
    public XRFrameBuffer? Target
    {
        get => _target;
        internal set
        {
            ThrowIfSealedForFramePlan();
            _target = value;
        }
    }
    public FrameOpContext Context
    {
        get => _context;
        internal set
        {
            ThrowIfSealedForFramePlan();
            _context = value;
        }
    }
    internal ref readonly FrameOpContext ContextReference => ref _context;
    internal FrameOpResourceUseList ResourceUses
    {
        get => _resourceUses;
        private set => _resourceUses = value;
    }
    internal ref readonly FrameOpResourceUseList ResourceUsesReference
        => ref _resourceUses;
    public abstract EVulkanPrimaryPlanNodeKind Kind { get; }

    /// <summary>
    /// Gets whether this operation participates in the normal context and pass
    /// preparation performed by the primary command recorder.
    /// </summary>
    internal virtual bool RequiresPrimaryRecordingContext => true;

    /// <summary>
    /// Routes this operation to its strongly typed primary-command handler.
    /// </summary>
    internal abstract int RecordPrimary(
        VulkanCommandRuntime commandRuntime,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo);

    /// <summary>
    /// Executes a planned secondary bucket shared by operations that support
    /// secondary-range recording.
    /// </summary>
    protected static bool TryRecordSecondaryBucket(
        VulkanCommandRuntime commandRuntime,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo,
        string label,
        out int lastOperationIndex)
    {
        lastOperationIndex = recordingInfo.OperationIndex;
        if (!recordingInfo.ExecutesSecondaryRange ||
            !VulkanCommandRuntime.TryGetSecondaryBucketForStart(
                recordingState.SecondaryBuckets,
                recordingState.SecondaryBucketByStart,
                recordingInfo.OperationIndex,
                out VulkanSecondaryRecordingBucket bucket) ||
            !commandRuntime.TryRecordSecondaryBucket(
                primaryCommandBuffer: recordingState.CommandBuffer,
                recordingState.FrameDataImageIndex,
                recordingState.ExecutedCommandChainSecondaryHandles,
                recordingState.Ops,
                recordingState.ScheduledCommandChainKeysByOpIndex,
                recordingState.ScheduledCommandChainCache,
                recordingInfo.OperationIndex,
                bucket,
                recordingInfo.PassIndex,
                recordingState.RenderGraphPlan.CompiledGraph.PassOrder.ContainsKey(
                    recordingInfo.PassIndex) ||
                recordingInfo.PassIndex == VulkanBarrierPlanner.SwapchainPassIndex,
                recordingState.RenderScope.IsActive,
                recordingState.ActiveInlineQuery is not null,
                label))
        {
            return false;
        }

        lastOperationIndex = recordingInfo.OperationIndex + bucket.Count - 1;
        return true;
    }

    /// <summary>
    /// Rents an operation whose lifetime is bounded by the current render frame.
    /// The same slot is not reused again until a later frame, so deferred command
    /// recording can safely retain references for the rest of this frame.
    /// </summary>
    protected static bool TryRentForCurrentFrame<T>(
        in FrameOpContext context,
        out T? reusable)
        where T : FrameOp
    {
        reusable = null;
        if (context.OperationWorkspace is null ||
            RuntimeRenderingHostServices.FrameTiming.CurrentRenderPipelineContext is null)
            return false;

        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (frameId == 0)
            return false;

        return context.OperationWorkspace.TryRent(frameId, out reusable);
    }

    /// <summary>
    /// Prevents a current-frame pool entry from being reset for a later frame
    /// while a prepared worker still owns a sealed frame-plan reference.
    /// </summary>
    internal void AcquireFramePlanLease()
        => Interlocked.Increment(ref _framePlanLeaseCount);

    internal void ReleaseFramePlanLease()
    {
        int remaining = Interlocked.Decrement(ref _framePlanLeaseCount);
        if (remaining < 0)
        {
            Interlocked.Increment(ref _framePlanLeaseCount);
            throw new InvalidOperationException("Frame-operation lease underflow.");
        }
    }

    internal void SetResourceUses(in FrameOpResourceUseList resourceUses)
    {
        ThrowIfSealedForFramePlan();
        ResourceUses = resourceUses;
    }

    internal ref FrameOpResourceUseList BeginResourceUseUpdate()
    {
        ThrowIfSealedForFramePlan();
        _resourceUses.Clear();
        return ref _resourceUses;
    }

    /// <summary>
    /// Copies this frame-local producer operation into the plan-owned immutable
    /// stream. The plan never retains a pooled producer instance.
    /// </summary>
    internal virtual FrameOp CreateSealedPlanSnapshot()
    {
        ThrowIfSealedForFramePlan();
        return SealPlanSnapshot(this with { });
    }

    /// <summary>Marks an already detached operation copy immutable for plan ownership.</summary>
    protected T SealPlanSnapshot<T>(T snapshot)
        where T : FrameOp
    {
        snapshot._isSealedForFramePlan = true;
        return snapshot;
    }

    protected void ThrowIfSealedForFramePlan()
    {
        if (_isSealedForFramePlan)
            throw new InvalidOperationException("A sealed frame-plan operation cannot be mutated.");
    }

    internal bool IsSealedForFramePlan => _isSealedForFramePlan;

    internal bool IsPinnedByFramePlan
        => Volatile.Read(ref _framePlanLeaseCount) != 0;

    protected static T RetainForCurrentFrame<T>(T created, in FrameOpContext context)
        where T : FrameOp
    {
        return context.OperationWorkspace?.Retain(created) ?? created;
    }
}

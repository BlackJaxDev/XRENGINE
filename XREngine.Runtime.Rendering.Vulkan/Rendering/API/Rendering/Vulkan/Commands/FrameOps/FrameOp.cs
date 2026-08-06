using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal abstract record FrameOp(int PassIndex, XRFrameBuffer? Target, FrameOpContext Context)
{
    private int _framePlanLeaseCount;
    private bool _isSealedForFramePlan;
    private int _passIndex = PassIndex;
    private XRFrameBuffer? _target = Target;
    private FrameOpContext _context = Context;

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
    internal FrameOpResourceUseList ResourceUses { get; private set; }
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
        VulkanRenderer renderer,
        scoped ref VulkanRenderer.PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo);

    /// <summary>
    /// Executes a planned secondary bucket shared by operations that support
    /// secondary-range recording.
    /// </summary>
    protected static bool TryRecordSecondaryBucket(
        VulkanRenderer renderer,
        scoped ref VulkanRenderer.PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo,
        string label,
        out int lastOperationIndex)
    {
        lastOperationIndex = recordingInfo.OperationIndex;
        if (!recordingInfo.ExecutesSecondaryRange ||
            !VulkanRenderer.TryGetSecondaryBucketForStart(
                recordingState.SecondaryBuckets,
                recordingState.SecondaryBucketByStart,
                recordingInfo.OperationIndex,
                out VulkanSecondaryRecordingBucket bucket) ||
            !renderer.TryRecordSecondaryBucket(
                primaryCommandBuffer: recordingState.CommandBuffer,
                recordingState.FrameDataImageIndex,
                recordingState.ExecutedCommandChainSecondaryHandles,
                recordingState.Ops,
                recordingState.ScheduledCommandChainKeysByOpIndex,
                recordingState.ScheduledCommandChainCache,
                recordingInfo.OperationIndex,
                bucket,
                recordingInfo.PassIndex,
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
    protected static bool TryRentForCurrentFrame<T>(out T? reusable)
        where T : FrameOp
    {
        reusable = null;
        if (RuntimeRenderingHostServices.FrameTiming.CurrentRenderPipelineContext is null)
            return false;

        ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (frameId == 0)
            return false;

        if (FramePool<T>.FrameId != frameId)
        {
            FramePool<T>.FrameId = frameId;
            FramePool<T>.Cursor = 0;
        }

        List<T> pool = FramePool<T>.Items ??= [];
        int slot = FramePool<T>.Cursor;
        while (slot < pool.Count)
        {
            T candidate = pool[slot++];
            if (candidate.IsPinnedByFramePlan)
                continue;

            reusable = candidate;
            break;
        }

        // Reserving an absent slot prevents the object just appended by
        // RetainForCurrentFrame from being rented again later this frame.
        FramePool<T>.Cursor = reusable is null ? slot + 1 : slot;

        return true;
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

    private bool IsPinnedByFramePlan
        => Volatile.Read(ref _framePlanLeaseCount) != 0;

    protected static T RetainForCurrentFrame<T>(T created)
        where T : FrameOp
    {
        (FramePool<T>.Items ??= []).Add(created);
        return created;
    }

    internal static void ReleaseCurrentThreadPools()
    {
        FramePool<ClearOp>.ReleaseCurrentThread();
        FramePool<MeshDrawOp>.ReleaseCurrentThread();
        FramePool<IndirectDrawOp>.ReleaseCurrentThread();
        FramePool<MemoryBarrierOp>.ReleaseCurrentThread();
        FramePool<ComputeDispatchOp>.ReleaseCurrentThread();
    }

    private static class FramePool<T>
        where T : FrameOp
    {
        private static readonly ThreadLocal<PoolState> ThreadState =
            new(static () => new PoolState(), trackAllValues: false);

        private static PoolState Current
            => ThreadState.Value
                ?? throw new InvalidOperationException(
                    "The Vulkan frame-operation pool has been disposed.");

        internal static List<T>? Items
        {
            get => Current.Items;
            set => Current.Items = value;
        }

        internal static ulong FrameId
        {
            get => Current.FrameId;
            set => Current.FrameId = value;
        }

        internal static int Cursor
        {
            get => Current.Cursor;
            set => Current.Cursor = value;
        }

        internal static void ReleaseCurrentThread()
        {
            Current.Items?.Clear();
            Current.Items = null;
            Current.FrameId = 0;
            Current.Cursor = 0;
        }

        private sealed class PoolState
        {
            public List<T>? Items;
            public ulong FrameId;
            public int Cursor;
        }
    }
}

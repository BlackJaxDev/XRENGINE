using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal abstract record FrameOp(int PassIndex, XRFrameBuffer? Target, FrameOpContext Context)
{
    public int PassIndex { get; internal set; } = PassIndex;
    public XRFrameBuffer? Target { get; internal set; } = Target;
    public FrameOpContext Context { get; internal set; } = Context;

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
        int slot = FramePool<T>.Cursor++;
        if (slot < pool.Count)
            reusable = pool[slot];

        return true;
    }

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

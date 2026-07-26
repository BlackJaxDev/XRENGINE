namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
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
            FramePool<MemoryBarrierOp>.ReleaseCurrentThread();
            FramePool<ComputeDispatchOp>.ReleaseCurrentThread();
        }

        private static class FramePool<T>
            where T : FrameOp
        {
            [ThreadStatic]
            internal static List<T>? Items;
            [ThreadStatic]
            internal static ulong FrameId;
            [ThreadStatic]
            internal static int Cursor;

            internal static void ReleaseCurrentThread()
            {
                Items?.Clear();
                Items = null;
                FrameId = 0;
                Cursor = 0;
            }
        }
    }
}

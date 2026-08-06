using System;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable frame-operation storage owned by one renderer command worker.
/// A captured <see cref="FrameOpContext"/> carries this workspace explicitly.
/// </summary>
internal sealed class VulkanFrameOpWorkspace
{
    private readonly Dictionary<Type, object> _pools = new()
    {
        [typeof(ClearOp)] = new Pool<ClearOp>(),
        [typeof(MeshDrawOp)] = new Pool<MeshDrawOp>(),
        [typeof(IndirectDrawOp)] = new Pool<IndirectDrawOp>(),
        [typeof(MemoryBarrierOp)] = new Pool<MemoryBarrierOp>(),
        [typeof(ComputeDispatchOp)] = new Pool<ComputeDispatchOp>(),
    };

    internal bool TryRent<T>(ulong frameId, out T? reusable)
        where T : FrameOp
    {
        Pool<T> state = GetPool<T>();
        if (state.FrameId != frameId)
        {
            state.FrameId = frameId;
            state.Cursor = 0;
        }

        reusable = null;
        int slot = state.Cursor;
        while (slot < state.Items.Count)
        {
            T candidate = state.Items[slot++];
            if (candidate.IsPinnedByFramePlan)
                continue;

            reusable = candidate;
            break;
        }

        state.Cursor = reusable is null ? slot + 1 : slot;
        return true;
    }

    internal T Retain<T>(T created)
        where T : FrameOp
    {
        GetPool<T>().Items.Add(created);
        return created;
    }

    internal void Reset()
    {
        foreach (object pool in _pools.Values)
            ((IFrameOpPool)pool).Reset();
    }

    private Pool<T> GetPool<T>()
        where T : FrameOp
        => (Pool<T>)_pools[typeof(T)];

    private interface IFrameOpPool
    {
        void Reset();
    }

    private sealed class Pool<T> : IFrameOpPool
        where T : FrameOp
    {
        internal readonly List<T> Items = [];
        internal ulong FrameId;
        internal int Cursor;

        public void Reset()
        {
            Items.Clear();
            FrameId = 0;
            Cursor = 0;
        }
    }
}

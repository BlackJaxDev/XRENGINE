using System;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable frame-operation storage owned by one renderer command worker.
/// A captured <see cref="FrameOpContext"/> carries this workspace explicitly.
/// </summary>
internal sealed class VulkanFrameOpWorkspace
{
    private readonly Pool<ClearOp> _clearOps = new();
    private readonly Pool<MeshDrawOp> _meshDrawOps = new();
    private readonly Pool<IndirectDrawOp> _indirectDrawOps = new();
    private readonly Pool<MemoryBarrierOp> _memoryBarrierOps = new();
    private readonly Pool<ComputeDispatchOp> _computeDispatchOps = new();

    internal bool TryRent<T>(ulong frameId, out T? reusable)
        where T : FrameOp
    {
        Pool<T> state = GetPool<T>();
        if (state.FrameId != frameId)
        {
            state.FrameId = frameId;
            state.Cursor = 0;
        }

        int slot = state.Cursor++;
        reusable = slot < state.Items.Count
            ? state.Items[slot]
            : null;
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
        _clearOps.Reset();
        _meshDrawOps.Reset();
        _indirectDrawOps.Reset();
        _memoryBarrierOps.Reset();
        _computeDispatchOps.Reset();
    }

    private Pool<T> GetPool<T>()
        where T : FrameOp
    {
        if (typeof(T) == typeof(ClearOp))
            return (Pool<T>)(object)_clearOps;
        if (typeof(T) == typeof(MeshDrawOp))
            return (Pool<T>)(object)_meshDrawOps;
        if (typeof(T) == typeof(IndirectDrawOp))
            return (Pool<T>)(object)_indirectDrawOps;
        if (typeof(T) == typeof(MemoryBarrierOp))
            return (Pool<T>)(object)_memoryBarrierOps;
        if (typeof(T) == typeof(ComputeDispatchOp))
            return (Pool<T>)(object)_computeDispatchOps;

        throw new NotSupportedException($"Frame-operation pooling is not configured for {typeof(T).FullName}.");
    }

    private sealed class Pool<T>
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

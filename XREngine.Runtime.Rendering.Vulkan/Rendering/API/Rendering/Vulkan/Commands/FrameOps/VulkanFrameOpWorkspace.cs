using System;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable frame-operation storage owned by one renderer command worker.
/// A captured <see cref="FrameOpContext"/> carries this workspace explicitly.
/// </summary>
/// <remarks>
/// The default workspace reuses an operation in the next render frame. A
/// workspace created with a reuse distance keeps each operation for that many
/// render frames before reuse. Captured work, such as OpenXR eye operations,
/// can stay referenced after its render frame ends, so it uses a distance that
/// covers the frames in flight. The distance mode is safe to use from more than
/// one thread.
/// </remarks>
internal sealed class VulkanFrameOpWorkspace
{
    private readonly ulong _reuseFrameDistance;
    private readonly Lock? _distanceSync;

    internal VulkanFrameOpWorkspace()
    {
    }

    /// <param name="reuseFrameDistance">
    /// Number of render frames that must pass before an operation is reused.
    /// </param>
    internal VulkanFrameOpWorkspace(int reuseFrameDistance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reuseFrameDistance, 1);
        _reuseFrameDistance = (ulong)reuseFrameDistance;
        _distanceSync = new Lock();
    }

    private readonly Pool<ClearOp> _clearOps = new();
    private readonly Pool<MeshDrawOp> _meshDrawOps = new();
    private readonly Pool<IndirectDrawOp> _indirectDrawOps = new();
    private readonly Pool<MemoryBarrierOp> _memoryBarrierOps = new();
    private readonly Pool<ComputeDispatchOp> _computeDispatchOps = new();
    private readonly Pool<BufferCopyOp> _bufferCopyOps = new();
    private readonly Pool<AdvancedVisibilityOp> _advancedVisibilityOps = new();

    internal bool TryRent<T>(ulong frameId, out T? reusable)
        where T : FrameOp
    {
        Pool<T> state = GetPool<T>();
        if (_distanceSync is not null)
        {
            lock (_distanceSync)
                reusable = TryRentAfterDistance(state, frameId);
            return true;
        }

        if (state.FrameId != frameId)
        {
            state.FrameId = frameId;
            state.Cursor = 0;
        }

        int slot = state.Cursor;
        while (slot < state.Items.Count && !state.Items[slot].CanReuseFromPool)
            ++slot;
        state.Cursor = slot + 1;
        reusable = slot < state.Items.Count
            ? state.Items[slot]
            : null;
        return true;
    }

    internal T Retain<T>(T created)
        where T : FrameOp
    {
        Pool<T> state = GetPool<T>();
        if (_distanceSync is null)
        {
            state.Items.Add(created);
            return created;
        }

        lock (_distanceSync)
        {
            // Keep the ring ordered by rent frame: the new operation is the most
            // recent one, so it goes just before the oldest candidate.
            int index = Math.Min(state.Cursor, state.Items.Count);
            state.Items.Insert(index, created);
            state.RentFrames.Insert(index, RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId);
            state.Cursor = index + 1;
        }
        return created;
    }

    /// <summary>
    /// Returns the oldest operation of the ring when its last rent is at least
    /// the reuse distance old and it owns no authoring resource. Otherwise the
    /// caller creates a new operation, and the ring grows to its high-water mark.
    /// </summary>
    private T? TryRentAfterDistance<T>(Pool<T> state, ulong frameId)
        where T : FrameOp
    {
        int count = state.Items.Count;
        if (count == 0)
            return null;

        int index = state.Cursor >= count ? 0 : state.Cursor;
        T candidate = state.Items[index];
        if (state.RentFrames[index] + _reuseFrameDistance > frameId ||
            !candidate.CanReuseFromPool)
        {
            state.Cursor = index;
            return null;
        }

        state.RentFrames[index] = frameId;
        state.Cursor = index + 1;
        return candidate;
    }

    internal void Reset()
    {
        _clearOps.Reset();
        _meshDrawOps.Reset();
        _indirectDrawOps.Reset();
        _memoryBarrierOps.Reset();
        _computeDispatchOps.Reset();
        _bufferCopyOps.Reset();
        _advancedVisibilityOps.Reset();
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
        if (typeof(T) == typeof(BufferCopyOp))
            return (Pool<T>)(object)_bufferCopyOps;
        if (typeof(T) == typeof(AdvancedVisibilityOp))
            return (Pool<T>)(object)_advancedVisibilityOps;

        throw new NotSupportedException($"Frame-operation pooling is not configured for {typeof(T).FullName}.");
    }

    private sealed class Pool<T>
        where T : FrameOp
    {
        internal readonly List<T> Items = [];
        internal readonly List<ulong> RentFrames = [];
        internal ulong FrameId;
        internal int Cursor;

        public void Reset()
        {
            Items.Clear();
            RentFrames.Clear();
            FrameId = 0;
            Cursor = 0;
        }
    }
}

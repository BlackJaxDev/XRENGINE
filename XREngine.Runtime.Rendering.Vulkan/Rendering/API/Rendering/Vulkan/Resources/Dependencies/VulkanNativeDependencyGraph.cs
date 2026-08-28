using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Native-owner reverse dependency graph. It deliberately assigns its own stable
/// slot/generation identities instead of borrowing canonical-scene handles.
/// All mutation is serialized by this authority; normal recording may retain a
/// captured handle and generation without touching its dictionaries.
/// </summary>
internal sealed class VulkanNativeDependencyGraph
{
    private const int InvalidEdge = -1;
    private readonly object _gate = new();
    private readonly OwnerState[] _owners = new OwnerState[Enum.GetValues<EVulkanNativeDependencyOwner>().Length];
    private readonly Queue<VulkanNativeDependencyInvalidationRecord> _dirtyRecords = [];

    internal VulkanNativeDependencyGraph()
    {
        for (int index = 0; index < _owners.Length; ++index)
            _owners[index] = new OwnerState();
    }

    internal VulkanNativeDependencyHandle Register(EVulkanNativeDependencyOwner owner, ulong nativeHandle)
    {
        if (nativeHandle == 0)
            return default;

        lock (_gate)
        {
            OwnerState state = _owners[(int)owner];
            if (state.SlotsByNativeHandle.TryGetValue(nativeHandle, out uint slot))
                return new VulkanNativeDependencyHandle(slot, state.Slots[(int)slot].Generation);

            slot = state.FreeSlots.Count > 0 ? state.FreeSlots.Pop() : checked((uint)state.Slots.Count);
            SlotState slotState;
            if (slot == state.Slots.Count)
            {
                slotState = new SlotState
                {
                    Generation = 1,
                    NativeHandle = nativeHandle,
                    Live = true,
                };
                state.Slots.Add(slotState);
                state.ReverseHeads.Add(InvalidEdge);
            }
            else
            {
                slotState = state.Slots[(int)slot];
                slotState.Generation = Next(slotState.Generation);
                slotState.TopologyGeneration = Next(slotState.TopologyGeneration);
                slotState.ContentGeneration = Next(slotState.ContentGeneration);
                slotState.NativeHandle = nativeHandle;
                slotState.Live = true;
                state.Slots[(int)slot] = slotState;
            }
            state.SlotsByNativeHandle.Add(nativeHandle, slot);
            return new VulkanNativeDependencyHandle(slot, slotState.Generation);
        }
    }

    internal bool TryGet(EVulkanNativeDependencyOwner owner, ulong nativeHandle, out VulkanNativeDependencyHandle handle)
    {
        lock (_gate)
        {
            OwnerState state = _owners[(int)owner];
            if (nativeHandle != 0 && state.SlotsByNativeHandle.TryGetValue(nativeHandle, out uint slot))
            {
                handle = new VulkanNativeDependencyHandle(slot, state.Slots[(int)slot].Generation);
                return true;
            }
        }
        handle = default;
        return false;
    }

    internal bool TryGetGeneration(EVulkanNativeDependencyOwner owner, VulkanNativeDependencyHandle handle, out VulkanNativeDependencyGeneration generation)
    {
        lock (_gate)
        {
            if (TryGetLiveSlotNoLock(owner, handle, out SlotState? slot))
            {
                generation = new VulkanNativeDependencyGeneration(slot!.TopologyGeneration, slot.ContentGeneration);
                return true;
            }
        }
        generation = default;
        return false;
    }

    /// <summary>Resolves an exact live graph identity back to its native handle.</summary>
    internal bool TryGetNativeHandle(
        EVulkanNativeDependencyOwner owner,
        VulkanNativeDependencyHandle handle,
        out ulong nativeHandle)
    {
        lock (_gate)
        {
            if (TryGetLiveSlotNoLock(owner, handle, out SlotState? slot))
            {
                nativeHandle = slot!.NativeHandle;
                return true;
            }
        }

        nativeHandle = 0;
        return false;
    }

    internal bool Link(
        EVulkanNativeDependencyOwner sourceOwner,
        VulkanNativeDependencyHandle source,
        EVulkanNativeDependencyOwner dependentOwner,
        VulkanNativeDependencyHandle dependent)
    {
        lock (_gate)
        {
            if (!TryGetLiveSlotNoLock(sourceOwner, source, out _) ||
                !TryGetLiveSlotNoLock(dependentOwner, dependent, out _))
            {
                return false;
            }

            OwnerState sourceState = _owners[(int)sourceOwner];
            for (int existingIndex = sourceState.ReverseHeads[(int)source.Slot];
                 existingIndex != InvalidEdge;
                 existingIndex = sourceState.Edges[existingIndex].Next)
            {
                Edge existing = sourceState.Edges[existingIndex];
                if (existing.Active && existing.DependentOwner == dependentOwner &&
                    existing.Dependent == dependent)
                    return true;
            }
            int edgeIndex = sourceState.Edges.Count;
            sourceState.Edges.Add(new Edge(dependentOwner, dependent, sourceState.ReverseHeads[(int)source.Slot]));
            sourceState.ReverseHeads[(int)source.Slot] = edgeIndex;
            return true;
        }
    }

    /// <summary>Removes one exact reverse edge without disturbing adjacent dependencies.</summary>
    internal bool Unlink(
        EVulkanNativeDependencyOwner sourceOwner,
        VulkanNativeDependencyHandle source,
        EVulkanNativeDependencyOwner dependentOwner,
        VulkanNativeDependencyHandle dependent)
    {
        lock (_gate)
        {
            if (!TryGetLiveSlotNoLock(sourceOwner, source, out _))
                return false;

            OwnerState sourceState = _owners[(int)sourceOwner];
            for (int edgeIndex = sourceState.ReverseHeads[(int)source.Slot];
                 edgeIndex != InvalidEdge;
                 edgeIndex = sourceState.Edges[edgeIndex].Next)
            {
                Edge edge = sourceState.Edges[edgeIndex];
                if (!edge.Active || edge.DependentOwner != dependentOwner || edge.Dependent != dependent)
                    continue;

                edge.Active = false;
                sourceState.Edges[edgeIndex] = edge;
                return true;
            }
            return false;
        }
    }

    internal bool Mutate(EVulkanNativeDependencyOwner owner, VulkanNativeDependencyHandle handle, EVulkanNativeDependencyMutationDomain domain, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        lock (_gate)
        {
            if (!TryGetLiveSlotNoLock(owner, handle, out SlotState? slot))
                return false;

            if (domain == EVulkanNativeDependencyMutationDomain.Content)
                slot!.ContentGeneration = Next(slot.ContentGeneration);
            else
                slot!.TopologyGeneration = Next(slot.TopologyGeneration);

            PropagateMutationNoLock(owner, handle, domain, reason);
            return true;
        }
    }

    internal bool Retire(EVulkanNativeDependencyOwner owner, ulong nativeHandle, string reason)
    {
        lock (_gate)
        {
            OwnerState state = _owners[(int)owner];
            if (!state.SlotsByNativeHandle.Remove(nativeHandle, out uint slotIndex))
                return false;

            SlotState slot = state.Slots[(int)slotIndex];
            VulkanNativeDependencyHandle handle = new(slotIndex, slot.Generation);
            _ = MutateNoLock(owner, handle, EVulkanNativeDependencyMutationDomain.Retirement, reason);
            // Edge storage is append-only so a retained sealed artifact can
            // still be diagnosed by its captured identity. Clearing this flat
            // head is what prevents a reused slot from inheriting old edges.
            state.ReverseHeads[(int)slotIndex] = InvalidEdge;
            slot.Live = false;
            slot.NativeHandle = 0;
            state.Slots[(int)slotIndex] = slot;
            state.FreeSlots.Push(slotIndex);
            return true;
        }
    }

    internal bool TryDequeueDirtyRecord(out VulkanNativeDependencyInvalidationRecord record)
    {
        lock (_gate)
            return _dirtyRecords.TryDequeue(out record);
    }

    /// <summary>
    /// Removes one invalidation for the requested dependent ownership domain
    /// without allowing one consumer to discard another domain's work. The
    /// graph is a cold mutation-path authority, so the small queue rotation is
    /// preferable to a global drain plus per-consumer side queues.
    /// </summary>
    internal bool TryDequeueDirtyRecord(
        EVulkanNativeDependencyOwner dependentOwner,
        out VulkanNativeDependencyInvalidationRecord record)
    {
        lock (_gate)
        {
            int pendingCount = _dirtyRecords.Count;
            for (int index = 0; index < pendingCount; ++index)
            {
                VulkanNativeDependencyInvalidationRecord candidate = _dirtyRecords.Dequeue();
                if (candidate.DependentOwner == dependentOwner)
                {
                    record = candidate;
                    return true;
                }

                _dirtyRecords.Enqueue(candidate);
            }
        }

        record = default;
        return false;
    }

    private bool MutateNoLock(EVulkanNativeDependencyOwner owner, VulkanNativeDependencyHandle handle, EVulkanNativeDependencyMutationDomain domain, string reason)
    {
        SlotState? slot = TryGetLiveSlotNoLock(owner, handle, out SlotState? found) ? found : null;
        if (slot is null)
            return false;
        if (domain == EVulkanNativeDependencyMutationDomain.Content)
            slot.ContentGeneration = Next(slot.ContentGeneration);
        else
            slot.TopologyGeneration = Next(slot.TopologyGeneration);
        PropagateMutationNoLock(owner, handle, domain, reason);
        return true;
    }

    /// <summary>
    /// Walks the compact forward links to every reachable dependent. Native
    /// resources commonly form a chain (shader/layout/table -> pipeline ->
    /// resident variant); stopping at the first edge silently left the final
    /// reusable artifact warm after a source mutation.
    /// </summary>
    private void PropagateMutationNoLock(
        EVulkanNativeDependencyOwner sourceOwner,
        VulkanNativeDependencyHandle source,
        EVulkanNativeDependencyMutationDomain domain,
        string reason)
    {
        Queue<DependencyNode> pending = new();
        HashSet<DependencyNode> visited = [];
        pending.Enqueue(new DependencyNode(sourceOwner, source));
        visited.Add(new DependencyNode(sourceOwner, source));

        while (pending.TryDequeue(out DependencyNode current))
        {
            OwnerState state = _owners[(int)current.Owner];
            for (int edgeIndex = state.ReverseHeads[(int)current.Handle.Slot];
                 edgeIndex != InvalidEdge;
                 edgeIndex = state.Edges[edgeIndex].Next)
            {
                Edge edge = state.Edges[edgeIndex];
                if (!edge.Active ||
                    !TryGetLiveSlotNoLock(edge.DependentOwner, edge.Dependent, out _) ||
                    !visited.Add(new DependencyNode(edge.DependentOwner, edge.Dependent)))
                {
                    continue;
                }

                // Intermediate owners remain traversal nodes, but only terminal
                // reusable artifacts own invalidation work. Enqueuing pipeline
                // nodes left records that no authority consumed and made ordinary
                // descriptor writes look like structural residency mutations.
                if (edge.DependentOwner is EVulkanNativeDependencyOwner.ResidentVariant or
                    EVulkanNativeDependencyOwner.CommandArtifact)
                {
                    _dirtyRecords.Enqueue(new VulkanNativeDependencyInvalidationRecord(
                        sourceOwner,
                        source,
                        edge.DependentOwner,
                        edge.Dependent,
                        domain,
                        reason));
                }
                pending.Enqueue(new DependencyNode(edge.DependentOwner, edge.Dependent));
            }
        }
    }

    private bool TryGetLiveSlotNoLock(EVulkanNativeDependencyOwner owner, VulkanNativeDependencyHandle handle, out SlotState? slot)
    {
        slot = null;
        if (!handle.IsValid)
            return false;
        OwnerState state = _owners[(int)owner];
        if (handle.Slot >= (uint)state.Slots.Count)
            return false;
        SlotState candidate = state.Slots[(int)handle.Slot];
        if (!candidate.Live || candidate.Generation != handle.Generation)
            return false;
        slot = candidate;
        return true;
    }

    private static ulong Next(ulong generation) => generation == ulong.MaxValue ? 1UL : generation + 1UL;
    private static uint Next(uint generation) => generation == uint.MaxValue ? 1U : generation + 1U;

    private sealed class OwnerState
    {
        internal Dictionary<ulong, uint> SlotsByNativeHandle { get; } = [];
        internal List<SlotState> Slots { get; } = [new SlotState()];
        internal List<int> ReverseHeads { get; } = [InvalidEdge];
        internal Stack<uint> FreeSlots { get; } = [];
        internal List<Edge> Edges { get; } = [];
    }

    private sealed class SlotState
    {
        internal ulong NativeHandle;
        internal uint Generation;
        internal ulong TopologyGeneration = 1;
        internal ulong ContentGeneration = 1;
        internal bool Live = true;
    }

    private record struct Edge(
        EVulkanNativeDependencyOwner DependentOwner,
        VulkanNativeDependencyHandle Dependent,
        int Next)
    {
        internal bool Active = true;
    }

    private readonly record struct DependencyNode(
        EVulkanNativeDependencyOwner Owner,
        VulkanNativeDependencyHandle Handle);
}

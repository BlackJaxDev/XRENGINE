namespace XREngine.Rendering.Vulkan;

/// <summary>Resource-runtime-owned immutable-publication lowering keyed by the exact writable frame-slot epoch.</summary>
internal sealed class VulkanReadOnlyStoragePreparedMap
{
    private readonly object _sync = new();
    private readonly Dictionary<Key, VulkanFrameDataSlice> _slices = [];
    private readonly List<Key> _staleKeys = [];

    internal void Clear()
    {
        lock (_sync)
        {
            _slices.Clear();
            _staleKeys.Clear();
        }
    }

    internal VulkanReadOnlyStoragePreparedAuthority CreateAuthority(
        VulkanFrameDataArena arena,
        int frameSlot)
    {
        ulong resetEpoch = arena.GetFrameSlotResetEpoch(frameSlot);
        lock (_sync)
        {
            _staleKeys.Clear();
            foreach (Key key in _slices.Keys)
                if (key.ArenaIdentity == arena.Identity &&
                    key.ArenaGeneration == arena.Generation &&
                    key.FrameSlot == frameSlot && key.ResetEpoch != resetEpoch)
                {
                    _staleKeys.Add(key);
                }
            foreach (Key key in _staleKeys)
                _slices.Remove(key);
        }
        return new(this, arena, arena.Identity, arena.Generation, frameSlot, resetEpoch);
    }

    internal bool TryPrepare(
        in VulkanReadOnlyStoragePreparedAuthority authority,
        VulkanFrameDataArena arena,
        ReadOnlyStorageBindingSet bindings,
        out string reason)
    {
        if (!authority.IsCurrent || !ReferenceEquals(authority.Arena, arena))
        {
            reason = "The Vulkan immutable storage authority no longer matches the current frame-slot epoch.";
            return false;
        }
        lock (_sync)
        {
            foreach (ReadOnlyStorageBinding binding in bindings.Bindings)
            {
                Key key = new(authority, binding);
                if (_slices.ContainsKey(key))
                    continue;
                if (!arena.TryAllocate(authority.FrameSlot, EVulkanFrameDataLane.Storage,
                        (uint)binding.Length, 16u, out VulkanFrameDataSlice slice) ||
                    !arena.TryBeginWrite(slice, out VulkanFrameDataWriteScope scope))
                {
                    reason = "The Vulkan frame storage arena could not materialize immutable storage.";
                    return false;
                }
                using (scope)
                    binding.Publication.CopyRangeTo(binding.Offset, scope.Bytes);
                _slices.Add(key, slice);
            }
        }
        reason = string.Empty;
        return true;
    }

    internal bool TryResolve(in VulkanReadOnlyStoragePreparedAuthority authority,
        ReadOnlyStorageBinding binding, out VulkanFrameDataSlice slice)
    {
        if (!authority.IsCurrent)
        {
            slice = default;
            return false;
        }
        lock (_sync)
            return _slices.TryGetValue(new Key(authority, binding), out slice);
    }

    private readonly record struct Key(
        ulong ArenaIdentity, ulong ArenaGeneration, int FrameSlot, ulong ResetEpoch,
        ulong PublicationTokenId, ulong AbiSignature, int Offset, int Length)
    {
        internal Key(in VulkanReadOnlyStoragePreparedAuthority authority, ReadOnlyStorageBinding binding)
            : this(authority.ArenaIdentity, authority.ArenaGeneration, authority.FrameSlot,
                authority.ResetEpoch, binding.Publication.TokenId,
                binding.Publication.AbiSignature, binding.Offset, binding.Length) { }
    }
}

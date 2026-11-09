namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns one arena range until all contributors and recorded readers release it.
/// </summary>
internal sealed class VulkanMappedFrameAllocation
{
    private readonly Lock _gate = new();
    private readonly Action<VulkanMappedFrameAllocation> _onUnused;
    private readonly HashSet<object> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ulong> _commands = [];
    private int _authoringReaders;
    private bool _open = true;
    private bool _recycled;

    internal VulkanMappedFrameAllocation(
        ulong id,
        ulong arenaIdentity,
        ulong offset,
        uint length,
        ulong generation,
        object owner,
        Action<VulkanMappedFrameAllocation> onUnused)
    {
        Id = id;
        ArenaIdentity = arenaIdentity;
        Offset = offset;
        Length = length;
        Generation = generation;
        _onUnused = onUnused;
        _owners.Add(owner);
    }

    internal ulong Id { get; }
    internal ulong ArenaIdentity { get; }
    internal ulong Offset { get; }
    internal uint Length { get; }
    internal ulong Generation { get; }
    internal bool IsOpen
    {
        get
        {
            lock (_gate)
                return _open;
        }
    }

    internal bool HasOwner(object owner)
    {
        lock (_gate)
            return _owners.Contains(owner);
    }

    internal bool TryAddOwner(object owner)
    {
        lock (_gate)
        {
            if (!_open)
                return false;
            _owners.Add(owner);
            return true;
        }
    }

    internal bool ReleaseOwner(
        object owner,
        out ulong[] commandsToInvalidate,
        out bool recycle)
    {
        lock (_gate)
        {
            commandsToInvalidate = [];
            recycle = false;
            if (!_owners.Remove(owner) || _owners.Count != 0)
                return false;
            _open = false;
            commandsToInvalidate = [.. _commands];
            recycle = TryMarkUnused_NoLock();
        }
        return true;
    }

    internal bool TryRetainAuthoring()
    {
        lock (_gate)
        {
            if (!_open)
                return false;
            _authoringReaders++;
            return true;
        }
    }

    internal void ReleaseAuthoring()
    {
        bool recycle;
        lock (_gate)
        {
            if (_authoringReaders == 0)
                throw new InvalidOperationException("Mapped-frame allocation has no authoring reader to release.");
            _authoringReaders--;
            recycle = TryMarkUnused_NoLock();
        }
        if (recycle)
            _onUnused(this);
    }

    internal bool TryRetainCommand(ulong handle)
    {
        if (handle == 0)
            return false;
        lock (_gate)
        {
            if (!_open)
                return false;
            _commands.Add(handle);
            return true;
        }
    }

    internal void ReleaseCommand(ulong handle)
    {
        bool recycle;
        lock (_gate)
        {
            if (!_commands.Remove(handle))
                return;
            recycle = TryMarkUnused_NoLock();
        }
        if (recycle)
            _onUnused(this);
    }

    internal void CloseGeneration()
    {
        lock (_gate)
        {
            _open = false;
            _recycled = true;
            _owners.Clear();
        }
    }

    internal void NotifyUnused() => _onUnused(this);

    private bool TryMarkUnused_NoLock()
    {
        if (_open || _recycled || _authoringReaders != 0 || _commands.Count != 0)
            return false;
        _recycled = true;
        return true;
    }
}

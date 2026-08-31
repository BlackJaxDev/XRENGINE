namespace XREngine.Rendering;

/// <summary>Generation-checked owning token for a fixed immutable storage binding set.</summary>
public readonly struct ReadOnlyStorageBindingSet : IDisposable
{
    private const int MaximumBindings = 16;
    private static readonly Pool s_pool = new(1024);
    private readonly Entry? _entry;
    private readonly ulong _generation;

    private ReadOnlyStorageBindingSet(Entry entry, ulong generation)
        => (_entry, _generation) = (entry, generation);

    /// <summary>Forces bounded metadata allocation before a render hot path publishes bindings.</summary>
    public static void Prewarm() => _ = s_pool;

    public ReadOnlySpan<ReadOnlyStorageBinding> Bindings
    {
        get
        {
            Entry entry = GetLiveEntry();
            return entry.Bindings.AsSpan(0, entry.Count);
        }
    }

    public bool TryGet(uint binding, out ReadOnlyStorageBinding value)
    {
        ReadOnlySpan<ReadOnlyStorageBinding> bindings = Bindings;
        int low = 0;
        int high = bindings.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (bindings[middle].Binding == binding) { value = bindings[middle]; return true; }
            if (bindings[middle].Binding < binding) low = middle + 1;
            else high = middle - 1;
        }
        value = default;
        return false;
    }

    /// <summary>Creates a new sorted immutable set without list or delegate allocation.</summary>
    internal static ReadOnlyStorageBindingSet WithBinding(ReadOnlyStorageBindingSet? current, ReadOnlyStorageBinding replacement)
    {
        if (!replacement.IsValid)
            throw new ArgumentException("A read-only storage binding has an invalid publication range.", nameof(replacement));
        if (current is { } existing && existing.TryGet(replacement.Binding, out ReadOnlyStorageBinding prior) &&
            prior.Publication.IsSameToken(replacement.Publication) &&
            prior.Offset == replacement.Offset && prior.Length == replacement.Length)
        {
            return existing.Retain();
        }
        ReadOnlySpan<ReadOnlyStorageBinding> source = current is { } currentSet
            ? currentSet.Bindings
            : [];
        int insertion = 0;
        while (insertion < source.Length && source[insertion].Binding < replacement.Binding) ++insertion;
        bool replace = insertion < source.Length && source[insertion].Binding == replacement.Binding;
        int count = source.Length + (replace ? 0 : 1);
        if (count > MaximumBindings) throw new InvalidOperationException($"Read-only storage binding sets support at most {MaximumBindings} bindings.");

        Entry entry = s_pool.Rent(out ulong generation);
        try
        {
            for (int index = 0; index < insertion; ++index)
            {
                entry.Bindings[index] = Retain(source[index]);
                entry.Count++;
            }
            entry.Bindings[insertion] = Retain(replacement);
            entry.Count++;
            for (int sourceIndex = insertion + (replace ? 1 : 0), destinationIndex = insertion + 1; sourceIndex < source.Length; ++sourceIndex, ++destinationIndex)
            {
                entry.Bindings[destinationIndex] = Retain(source[sourceIndex]);
                entry.Count++;
            }
            return new(entry, generation);
        }
        catch
        {
            s_pool.Abandon(entry, generation);
            ReleaseEntry(entry, generation);
            throw;
        }
    }

    internal ReadOnlyStorageBindingSet Retain()
    {
        Entry entry = GetLiveEntry();
        s_pool.Retain(entry, _generation);
        return new(entry, _generation);
    }

    public void Dispose()
    {
        if (_entry is { } entry) s_pool.Release(entry, _generation);
    }

    private static ReadOnlyStorageBinding Retain(in ReadOnlyStorageBinding binding)
        => new(binding.Binding, binding.Publication.Retain(), binding.Offset, binding.Length);
    private Entry GetLiveEntry()
    {
        if (_entry is not { } entry || !s_pool.IsLive(entry, _generation)) throw new ObjectDisposedException(nameof(ReadOnlyStorageBindingSet));
        return entry;
    }
    private static void ReleaseEntry(Entry entry, ulong generation)
    {
        for (int index = 0; index < entry.Count; ++index) { entry.Bindings[index].Publication.Dispose(); entry.Bindings[index] = default; }
        entry.Count = 0;
        s_pool.Return(entry, generation);
    }

    private sealed class Entry
    {
        internal readonly ReadOnlyStorageBinding[] Bindings = new ReadOnlyStorageBinding[MaximumBindings];
        internal ulong Generation;
        internal int Count;
        internal int References;
    }
    private sealed class Pool(int capacity)
    {
        private readonly object _sync = new();
        private readonly Entry[] _entries = CreateEntries(capacity);
        private readonly int[] _free = CreateFree(capacity);
        private int _freeCount = capacity;
        private ulong _nextGeneration;
        internal Entry Rent(out ulong generation)
        {
            lock (_sync)
            {
                if (_freeCount == 0) throw new InvalidOperationException("Read-only storage binding set pool exhausted.");
                Entry entry = _entries[_free[--_freeCount]];
                entry.Generation = ++_nextGeneration == 0 ? ++_nextGeneration : _nextGeneration;
                entry.Count = 0; entry.References = 1; generation = entry.Generation; return entry;
            }
        }
        internal bool IsLive(Entry entry, ulong generation) { lock (_sync) return entry.Generation == generation && entry.References > 0; }
        internal void Retain(Entry entry, ulong generation)
        {
            lock (_sync) { if (entry.Generation != generation || entry.References <= 0) throw new ObjectDisposedException(nameof(ReadOnlyStorageBindingSet)); checked { entry.References++; } }
        }
        internal void Release(Entry entry, ulong generation)
        {
            bool released;
            lock (_sync) { if (entry.Generation != generation || entry.References <= 0) throw new ObjectDisposedException(nameof(ReadOnlyStorageBindingSet)); released = --entry.References == 0; }
            if (released) ReleaseEntry(entry, generation);
        }
        internal void Return(Entry entry, ulong generation)
        {
            lock (_sync) { if (entry.Generation != generation || entry.References != 0) throw new ObjectDisposedException(nameof(ReadOnlyStorageBindingSet)); _free[_freeCount++] = Array.IndexOf(_entries, entry); }
        }
        internal void Abandon(Entry entry, ulong generation)
        {
            lock (_sync)
            {
                if (entry.Generation != generation || entry.References != 1) throw new ObjectDisposedException(nameof(ReadOnlyStorageBindingSet));
                entry.References = 0;
            }
        }
        private static Entry[] CreateEntries(int capacity) { Entry[] entries = new Entry[capacity]; for (int i = 0; i < capacity; ++i) entries[i] = new Entry(); return entries; }
        private static int[] CreateFree(int capacity) { int[] free = new int[capacity]; for (int i = 0; i < capacity; ++i) free[i] = capacity - i - 1; return free; }
    }
}

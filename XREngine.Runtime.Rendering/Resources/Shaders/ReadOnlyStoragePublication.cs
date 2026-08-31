namespace XREngine.Rendering;

/// <summary>
/// Generation-checked value token for immutable CPU-owned storage bytes. The token has no managed
/// wrapper allocation; callers transfer ownership only by explicitly calling <see cref="Retain"/>.
/// </summary>
public readonly struct ReadOnlyStoragePublication : IDisposable
{
    private const int MaximumPublicationBytes = 64 * 1024;
    private const int PublicationEntryCapacity = 256;
    private static readonly PublicationEntryPool s_pool = new(PublicationEntryCapacity, MaximumPublicationBytes);

    private readonly PublicationEntry? _entry;
    private readonly ulong _entryGeneration;

    private ReadOnlyStoragePublication(PublicationEntry entry, ulong entryGeneration)
        => (_entry, _entryGeneration) = (entry, entryGeneration);

    public bool IsValid => _entry is { } entry && s_pool.IsLive(entry, _entryGeneration);
    public ulong OwnerId => GetLiveEntry().OwnerId;
    /// <summary>Globally unique pooled-entry generation used as the publication identity.</summary>
    public ulong TokenId => _entryGeneration;
    public ulong Generation => GetLiveEntry().PublicationGeneration;
    public ulong AbiSignature => GetLiveEntry().AbiSignature;
    public int Length => GetLiveEntry().Length;

    /// <summary>Prewarms the bounded private byte-entry pool before a render hot path uses it.</summary>
    public static void Prewarm() => _ = s_pool;

    /// <summary>
    /// Copies bytes into a bounded private entry. Exhaustion and oversize input are explicit so a
    /// renderer cannot silently retain mutable source storage or grow an unbounded pool.
    /// </summary>
    public static ReadOnlyStoragePublication CopyFrom(
        ReadOnlySpan<byte> source,
        ulong ownerId,
        ulong generation,
        ulong abiSignature)
    {
        if (source.Length <= 0 || source.Length > MaximumPublicationBytes)
            throw new ArgumentOutOfRangeException(
                nameof(source), source.Length,
                $"Read-only storage publications must be in 1..{MaximumPublicationBytes} bytes.");

        PublicationEntry entry = s_pool.Rent(source.Length, ownerId, generation, abiSignature, out ulong entryGeneration);
        source.CopyTo(entry.Bytes);
        return new ReadOnlyStoragePublication(entry, entryGeneration);
    }

    /// <summary>
    /// Copies unmanaged CPU-owned bytes into a bounded immutable publication without first allocating
    /// a managed staging array. The source must remain valid for the duration of this call only.
    /// </summary>
    internal static unsafe ReadOnlyStoragePublication CopyFrom(
        void* source,
        int length,
        ulong ownerId,
        ulong generation,
        ulong abiSignature)
    {
        if (source is null || length <= 0 || length > MaximumPublicationBytes)
            throw new ArgumentOutOfRangeException(
                nameof(length), length,
                $"Read-only storage publications must be in 1..{MaximumPublicationBytes} bytes.");

        PublicationEntry entry = s_pool.Rent(length, ownerId, generation, abiSignature, out ulong entryGeneration);
        new ReadOnlySpan<byte>(source, length).CopyTo(entry.Bytes);
        return new ReadOnlyStoragePublication(entry, entryGeneration);
    }

    /// <summary>Creates one additional owning token for queued work.</summary>
    internal ReadOnlyStoragePublication Retain()
    {
        PublicationEntry entry = GetLiveEntry();
        s_pool.Retain(entry, _entryGeneration);
        return new ReadOnlyStoragePublication(entry, _entryGeneration);
    }

    /// <summary>Checks entry identity and generation without exposing a pooled-entry reference.</summary>
    internal bool IsSameToken(in ReadOnlyStoragePublication other)
        => _entry is not null && ReferenceEquals(_entry, other._entry) && _entryGeneration == other._entryGeneration;

    /// <summary>Releases this explicitly owned token. Non-owning struct copies must not be disposed.</summary>
    public void Dispose()
    {
        if (_entry is { } entry)
            s_pool.Release(entry, _entryGeneration);
    }

    /// <summary>Copies every immutable byte while this token generation is held by the pool.</summary>
    public void CopyTo(Span<byte> destination)
        => s_pool.CopyTo(GetLiveEntry(), _entryGeneration, 0, destination);

    /// <summary>Copies an immutable subrange while this token generation is held by the pool.</summary>
    public void CopyRangeTo(int offset, Span<byte> destination)
        => s_pool.CopyTo(GetLiveEntry(), _entryGeneration, offset, destination);

    /// <summary>Compares candidate bytes without exposing a reusable backing array alias.</summary>
    public bool ByteContentEquals(ReadOnlySpan<byte> candidate)
        => s_pool.ContentEquals(GetLiveEntry(), _entryGeneration, candidate);

    private PublicationEntry GetLiveEntry()
    {
        if (_entry is not { } entry || !s_pool.IsLive(entry, _entryGeneration))
            throw new ObjectDisposedException(nameof(ReadOnlyStoragePublication));
        return entry;
    }

    private sealed class PublicationEntry(byte[] bytes)
    {
        public readonly byte[] Bytes = bytes;
        public ulong EntryGeneration;
        public ulong OwnerId;
        public ulong PublicationGeneration;
        public ulong AbiSignature;
        public int Length;
        public int ReferenceCount;
    }

    private sealed class PublicationEntryPool
    {
        private readonly object _sync = new();
        private readonly PublicationEntry[] _entries;
        private readonly int[] _freeEntries;
        private int _freeCount;
        private ulong _nextEntryGeneration;

        public PublicationEntryPool(int entryCapacity, int entryBytes)
        {
            _entries = new PublicationEntry[entryCapacity];
            _freeEntries = new int[entryCapacity];
            for (int index = 0; index < entryCapacity; ++index)
            {
                _entries[index] = new PublicationEntry(GC.AllocateUninitializedArray<byte>(entryBytes));
                _freeEntries[index] = entryCapacity - index - 1;
            }
            _freeCount = entryCapacity;
        }

        public PublicationEntry Rent(int length, ulong ownerId, ulong publicationGeneration, ulong abiSignature, out ulong entryGeneration)
        {
            lock (_sync)
            {
                if (_freeCount == 0)
                    throw new InvalidOperationException("Read-only storage publication pool exhausted; defer or release queued work before publishing more storage.");

                PublicationEntry entry = _entries[_freeEntries[--_freeCount]];
                entry.EntryGeneration = NextEntryGeneration();
                entry.OwnerId = ownerId;
                entry.PublicationGeneration = publicationGeneration;
                entry.AbiSignature = abiSignature;
                entry.Length = length;
                entry.ReferenceCount = 1;
                entryGeneration = entry.EntryGeneration;
                return entry;
            }
        }

        public bool IsLive(PublicationEntry entry, ulong entryGeneration)
        {
            lock (_sync)
                return entry.EntryGeneration == entryGeneration && entry.ReferenceCount > 0;
        }

        public void Retain(PublicationEntry entry, ulong entryGeneration)
        {
            lock (_sync)
            {
                if (entry.EntryGeneration != entryGeneration || entry.ReferenceCount <= 0)
                    throw new ObjectDisposedException(nameof(ReadOnlyStoragePublication));
                checked { entry.ReferenceCount++; }
            }
        }

        public void Release(PublicationEntry entry, ulong entryGeneration)
        {
            lock (_sync)
            {
                if (entry.EntryGeneration != entryGeneration || entry.ReferenceCount <= 0)
                    throw new ObjectDisposedException(nameof(ReadOnlyStoragePublication));
                if (--entry.ReferenceCount != 0)
                    return;

                entry.OwnerId = 0u;
                entry.PublicationGeneration = 0u;
                entry.AbiSignature = 0u;
                entry.Length = 0;
                _freeEntries[_freeCount++] = Array.IndexOf(_entries, entry);
            }
        }

        public void CopyTo(PublicationEntry entry, ulong entryGeneration, int offset, Span<byte> destination)
        {
            lock (_sync)
            {
                if (entry.EntryGeneration != entryGeneration || entry.ReferenceCount <= 0 ||
                    offset < 0 || offset > entry.Length || destination.Length > entry.Length - offset)
                {
                    throw new ObjectDisposedException(nameof(ReadOnlyStoragePublication));
                }

                entry.Bytes.AsSpan(offset, destination.Length).CopyTo(destination);
            }
        }

        public bool ContentEquals(PublicationEntry entry, ulong entryGeneration, ReadOnlySpan<byte> candidate)
        {
            lock (_sync)
            {
                if (entry.EntryGeneration != entryGeneration || entry.ReferenceCount <= 0)
                    throw new ObjectDisposedException(nameof(ReadOnlyStoragePublication));
                return candidate.SequenceEqual(entry.Bytes.AsSpan(0, entry.Length));
            }
        }

        private ulong NextEntryGeneration()
        {
            _nextEntryGeneration++;
            if (_nextEntryGeneration == 0u)
                _nextEntryGeneration++;
            return _nextEntryGeneration;
        }
    }
}

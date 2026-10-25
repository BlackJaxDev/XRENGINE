using XREngine.Core;

namespace System.Collections.Generic;

public partial class EventList<T>
{
    private readonly EventListMutationAuthority<T>? _mutationAuthority;
    private ulong _mutationRevision;

    /// <summary>Gets the permanent authority, when this list owns structural links.</summary>
    public EventListMutationAuthority<T>? GetMutationAuthority() => _mutationAuthority;

    /// <summary>
    /// Creates private structural storage with permanent authority. Input items are copied.
    /// Unrelated lists retain the normal mutation path and need no authority or reservation.
    /// </summary>
    public EventList(EventListMutationAuthority<T> authority, IEnumerable<T>? items = null,
        bool allowDuplicates = true, bool allowNull = true)
    {
        ArgumentNullException.ThrowIfNull(authority);
        _mutationAuthority = authority;
        _list = items is null ? [] : [.. items];
        _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        _allowDuplicates = allowDuplicates;
        _allowNull = allowNull;
        lock (authority.MutationGate)
            authority.Initialize(this, _list.AsReadOnly());
    }

    private static bool SameItem(T first, T second)
        => typeof(T).IsValueType ? EqualityComparer<T>.Default.Equals(first, second) : ReferenceEquals(first, second);

    private static bool ContainsSameItem(IReadOnlyList<T> items, T value)
    {
        for (int index = 0; index < items.Count; index++)
            if (SameItem(items[index], value)) return true;
        return false;
    }

    private int AuthorityIndexOf(T item)
    {
        for (int index = 0; index < _list.Count; index++)
            if (SameItem(_list[index], item))
                return index;
        return -1;
    }

    private int GuardedIndexOf(T item)
    {
        lock (_mutationAuthority!.MutationGate)
            return AuthorityIndexOf(item);
    }

    private ulong CaptureMutationRevision()
    {
        lock (_mutationAuthority!.MutationGate)
        {
            _mutationAuthority.RequireCurrent(this);
            return _mutationRevision;
        }
    }

    private void RequireMutationRevision(ulong revision, bool removal = false)
    {
        if (removal) _mutationAuthority!.RequireRemovalCurrent(this);
        else _mutationAuthority!.RequireCurrent(this);
        if (_mutationRevision != revision)
            throw new InvalidOperationException("The structural list changed during mutation callbacks.");
    }

    private bool GuardedInsert(T item, int? requestedIndex, bool reportItem, bool reportModified)
    {
        EventListMutationAuthority<T> authority = _mutationAuthority!;
        using EventListAuthorityLease<T> owner = new(authority, this);
        using EventListAuthorityLease<T> endpoint = new(authority, item, adding: true);
        ulong revision;
        lock (authority.MutationGate)
        {
            revision = _mutationRevision;
            if (!_allowNull && item is null || !_allowDuplicates && AuthorityIndexOf(item) >= 0)
                return false;
            if (requestedIndex is { } index && (uint)index > (uint)_list.Count)
                throw new ArgumentOutOfRangeException(nameof(requestedIndex));
        }
        if (!_updating)
        {
            if (reportItem)
            {
                if (requestedIndex is { } index)
                {
                    if (!(PreInserted?.Invoke(item, index) ?? true)) return false;
                }
                else if (!(PreAdded?.Invoke(item) ?? true)) return false;
                if (!(PreAnythingAdded?.Invoke(item) ?? true)) return false;
            }
            if (reportModified && !(PreModified?.Invoke() ?? true)) return false;
        }
        lock (authority.MutationGate)
        {
            RequireMutationRevision(revision);
            _list.EnsureCapacity(checked(_list.Count + 1));
            authority.PrepareAdd(item);
            _lock!.EnterWriteLock();
            try
            {
                if (requestedIndex is { } index) _list.Insert(index, item);
                else _list.Add(item);
                authority.Added(item);
                _mutationRevision++;
            }
            finally { _lock.ExitWriteLock(); }
        }
        if (!_updating)
        {
            if (reportItem)
            {
                if (requestedIndex is { } index) PostInserted?.Invoke(item, index);
                else PostAdded?.Invoke(item);
                PostAnythingAdded?.Invoke(item);
            }
            if (reportModified)
            {
                PostModified?.Invoke();
                CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Add, item));
            }
        }
        return true;
    }

    private void GuardedSetIndex(int index, T value)
    {
        EventListMutationAuthority<T> authority = _mutationAuthority!;
        using EventListAuthorityLease<T> owner = new(authority, this);
        using EventListAuthorityLease<T> added = new(authority, value, adding: true);
        T previous;
        ulong revision;
        lock (authority.MutationGate)
        {
            if ((uint)index >= (uint)_list.Count || !_allowNull && value is null ||
                !_allowDuplicates && AuthorityIndexOf(value) >= 0)
                return;
            previous = _list[index];
            revision = _mutationRevision;
        }
        using EventListAuthorityLease<T> removed = new(authority, previous, adding: false);
        if (!_updating && (!(PreModified?.Invoke() ?? true) || !(PreAdded?.Invoke(value) ?? true) ||
            !(PreAnythingAdded?.Invoke(value) ?? true) || !(PreIndexSet?.Invoke(index, value) ?? true)))
            return;
        lock (authority.MutationGate)
        {
            RequireMutationRevision(revision);
            authority.PrepareAdd(value);
            _lock!.EnterWriteLock();
            try
            {
                _list[index] = value;
                authority.Added(value);
                authority.Removed(previous);
                _mutationRevision++;
            }
            finally { _lock.ExitWriteLock(); }
        }
        if (!_updating)
        {
            PostAdded?.Invoke(value);
            PostAnythingAdded?.Invoke(value);
            PostIndexSet?.Invoke(index, previous);
            PostModified?.Invoke();
            CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Replace, value, index));
        }
    }

    private bool GuardedRemove(T value, int? requestedIndex, bool reportRemoved, bool reportModified, bool nonVetoable = false)
    {
        EventListMutationAuthority<T> authority = _mutationAuthority!;
        using EventListAuthorityLease<T> owner = new(authority, this, removal: true);
        T item;
        int index;
        ulong revision;
        lock (authority.MutationGate)
        {
            index = requestedIndex ?? AuthorityIndexOf(value);
            if (index < 0) return false;
            if ((uint)index >= (uint)_list.Count) throw new ArgumentOutOfRangeException(nameof(requestedIndex));
            item = _list[index];
            revision = _mutationRevision;
        }
        using EventListAuthorityLease<T> endpoint = new(authority, item, adding: false);
        if (!nonVetoable && !_updating)
        {
            if (reportModified && !(PreModified?.Invoke() ?? true)) return false;
            if (reportRemoved && (!(PreRemoved?.Invoke(item) ?? true) || !(PreAnythingRemoved?.Invoke(item) ?? true)))
                return false;
        }
        lock (authority.MutationGate)
        {
            RequireMutationRevision(revision, removal: true);
            _lock!.EnterWriteLock();
            try
            {
                _list.RemoveAt(index);
                authority.Removed(item);
                _mutationRevision++;
            }
            finally { _lock.ExitWriteLock(); }
        }
        if (nonVetoable)
            NotifyExactRemoval(item);
        else if (!_updating)
        {
            if (reportRemoved)
            {
                PostRemoved?.Invoke(item);
                PostAnythingRemoved?.Invoke(item);
            }
            if (reportModified)
            {
                PostModified?.Invoke();
                CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Remove, item));
            }
        }
        return true;
    }

    private void GuardedInsertRange(int? requestedIndex, IEnumerable<T> source, bool reportRange, bool reportModified,
        ulong? expectedRevision = null)
    {
        if (source is null) return;
        EventListMutationAuthority<T> authority = _mutationAuthority!;
        using EventListAuthorityLease<T> owner = new(authority, this);
        ulong revision = expectedRevision ?? CaptureMutationRevision();
        lock (authority.MutationGate) RequireMutationRevision(revision);
        // Range operations materialize once so enumerable callbacks cannot run inside the storage transaction.
        List<T> items = [];
        foreach (T item in source)
        {
            if (!_allowNull && item is null) continue;
            if (!_allowDuplicates && (GuardedIndexOf(item) >= 0 || ContainsSameItem(items, item))) continue;
            items.Add(item);
        }
        if (items.Count == 0) return;
        using EventListAuthorityLease<T> endpoints = new(authority, items, adding: true);
        lock (authority.MutationGate)
        {
            RequireMutationRevision(revision);
            if (requestedIndex is { } index && (uint)index > (uint)_list.Count)
                throw new ArgumentOutOfRangeException(nameof(requestedIndex));
        }
        IReadOnlyList<T> reserved = items;
        IEnumerable<T> notified = items.AsReadOnly();
        if (!_updating)
        {
            if (reportModified && !(PreModified?.Invoke() ?? true)) return;
            if (reportRange)
            {
                if (requestedIndex is { } index)
                {
                    if (!(PreInsertedRange?.Invoke(notified, index) ?? true)) return;
                }
                else if (!(PreAddedRange?.Invoke(notified) ?? true)) return;
                if (PreAnythingAdded is { } filter)
                {
                    items = [];
                    foreach (T item in reserved)
                        if (filter(item)) items.Add(item);
                    notified = items.AsReadOnly();
                }
            }
        }
        if (items.Count == 0) return;
        lock (authority.MutationGate)
        {
            RequireMutationRevision(revision);
            _list.EnsureCapacity(checked(_list.Count + items.Count));
            foreach (T item in items) authority.PrepareAdd(item);
            _lock!.EnterWriteLock();
            try
            {
                if (requestedIndex is { } index) _list.InsertRange(index, items);
                else _list.AddRange(items);
                foreach (T item in items) authority.Added(item);
                _mutationRevision++;
            }
            finally { _lock.ExitWriteLock(); }
        }
        if (!_updating)
        {
            if (reportRange)
            {
                if (requestedIndex is { } index) PostInsertedRange?.Invoke(notified, index);
                else PostAddedRange?.Invoke(notified);
                if (PostAnythingAdded is { } added)
                    foreach (T item in items) added(item);
            }
            if (reportModified)
            {
                PostModified?.Invoke();
                CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Add, items.AsReadOnly()));
            }
        }
    }

    private ulong? GuardedRemoveRange(int? start, int? count, Predicate<T>? match, bool clear,
        bool reportRange, bool reportModified, ulong? expectedRevision = null)
    {
        EventListMutationAuthority<T> authority = _mutationAuthority!;
        using EventListAuthorityLease<T> owner = new(authority, this, removal: true);
        List<IndexedItem> candidates = [];
        List<T> reserved = [];
        ulong revision;
        lock (authority.MutationGate)
        {
            revision = expectedRevision ?? _mutationRevision;
            RequireMutationRevision(revision, removal: true);
            int first = start ?? 0;
            int length = count ?? _list.Count;
            if (first < 0 || length < 0 || first > _list.Count - length)
                throw new ArgumentOutOfRangeException(nameof(start));
            for (int index = first; index < first + length; index++)
            {
                candidates.Add(new(index, _list[index]));
                reserved.Add(_list[index]);
            }
        }
        using EventListAuthorityLease<T> endpoints = new(authority, reserved, adding: false);
        if (match is not null)
            candidates = candidates.Where(candidate => match(candidate.Item)).ToList();
        List<T> removed = candidates.Select(candidate => candidate.Item).ToList();
        if (!_updating)
        {
            if (reportModified && !(PreModified?.Invoke() ?? true)) return null;
            if (reportRange)
            {
                if (!(PreRemovedRange?.Invoke(removed.AsReadOnly()) ?? true)) return null;
                if (PreAnythingRemoved is { } filter)
                {
                    candidates = candidates.Where(candidate => filter(candidate.Item)).ToList();
                    removed = candidates.Select(candidate => candidate.Item).ToList();
                }
            }
        }
        ulong completedRevision;
        lock (authority.MutationGate)
        {
            RequireMutationRevision(revision, removal: true);
            _lock!.EnterWriteLock();
            try
            {
                for (int index = candidates.Count - 1; index >= 0; index--)
                {
                    IndexedItem candidate = candidates[index];
                    _list.RemoveAt(candidate.Index);
                    authority.Removed(candidate.Item);
                }
                if (candidates.Count > 0) _mutationRevision++;
                completedRevision = _mutationRevision;
            }
            finally { _lock.ExitWriteLock(); }
        }
        if (!_updating)
        {
            if (reportRange)
            {
                PostRemovedRange?.Invoke(removed.AsReadOnly());
                if (PostAnythingRemoved is { } notify)
                    foreach (T item in removed) notify(item);
            }
            if (reportModified)
            {
                PostModified?.Invoke();
                CollectionChanged?.Invoke(this, clear
                    ? new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Clear)
                    : new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Remove, removed.AsReadOnly()));
            }
        }
        return completedRevision;
    }

    private void GuardedReorder(int? start, int? count, IComparer<T>? comparer, bool reverse, bool reportModified)
    {
        EventListMutationAuthority<T> authority = _mutationAuthority!;
        using EventListAuthorityLease<T> owner = new(authority, this);
        T[] reordered;
        ulong revision;
        lock (authority.MutationGate)
        {
            revision = _mutationRevision;
            reordered = [.. _list];
        }
        using EventListAuthorityLease<T> endpoints = new(authority, reordered, adding: false);
        bool report = reportModified && !_updating;
        if (report && !(PreModified?.Invoke() ?? true)) return;
        int first = start ?? 0;
        int length = count ?? reordered.Length;
        // Comparers can call application code. Sort the private copy before entering the write transaction.
        if (reverse) Array.Reverse(reordered, first, length);
        else Array.Sort(reordered, first, length, comparer);
        lock (authority.MutationGate)
        {
            RequireMutationRevision(revision);
            _lock!.EnterWriteLock();
            try
            {
                for (int index = 0; index < reordered.Length; index++) _list[index] = reordered[index];
                _mutationRevision++;
            }
            finally { _lock.ExitWriteLock(); }
        }
        if (report) PostModified?.Invoke();
    }

    private void GuardedSet(IEnumerable<T> source, bool reportRemoved, bool reportAdded, bool reportModified)
    {
        EventListMutationAuthority<T> authority = _mutationAuthority!;
        using EventListAuthorityLease<T> owner = new(authority, this);
        ulong revision = CaptureMutationRevision();
        T[] values = [.. source];
        using EventListAuthorityLease<T> endpoints = new(authority, values, adding: true);
        lock (authority.MutationGate) RequireMutationRevision(revision);
        ulong? clearedRevision = GuardedRemoveRange(null, null, null, clear: true,
            reportRemoved, reportModified: false, expectedRevision: revision);
        if (clearedRevision is { } expected)
            GuardedInsertRange(null, values, reportAdded, reportModified, expected);
    }

    private IEnumerable<T> GuardedFindAll(Predicate<T> match)
    {
        T[] values;
        _lock!.EnterReadLock();
        try { values = [.. _list]; }
        finally { _lock.ExitReadLock(); }
        // Predicates can mutate this list. Do not hold either storage lock while they run.
        return Array.FindAll(values, match);
    }
}

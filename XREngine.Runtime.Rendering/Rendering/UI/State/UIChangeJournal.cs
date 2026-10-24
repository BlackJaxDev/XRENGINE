using System;
using System.Collections.Generic;
using System.Threading;

namespace XREngine.Rendering.UI.State;

/// <summary>
/// Stores bounded UI invalidations as values. Callers serialize live UI tree writes.
/// The journal protects its own state, but it does not make the live tree thread-safe.
/// </summary>
public sealed class UIChangeJournal
{
    private static long s_nextJournalId;
    private readonly object _gate = new();
    private readonly long _journalId = Interlocked.Increment(ref s_nextJournalId);
    private readonly UIElementChange[] _changes;
    private readonly Dictionary<UIElementIdentity, int> _indices;
    private readonly UIElementChange[] _committedChanges;
    private readonly long[] _committedSequences;
    private readonly long[] _committedRevisions;
    private readonly UIElementChange[] _transactionChanges;
    private readonly Dictionary<UIElementIdentity, int> _transactionIndices;
    private int _count;
    private long _revision;
    private long _committedSequence;
    private long _oldestReadableSequence;
    private long _oldestReadableRevision;
    private int _committedStart;
    private int _committedCount;
    private int _transactionChangeCount;
    private bool _transactionChangesOverflowed;
    private UIChangeDomain _domains;
    private bool _requiresFullResync;
    private int _transactionOwnerThreadId;
    private int _transactionDepth;
    private bool _transactionDirty;
    private UIChangeTransactionScope? _topScope;

    /// <summary>Creates storage for the maximum number of distinct pending elements.</summary>
    public UIChangeJournal(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _changes = new UIElementChange[capacity];
        _indices = new Dictionary<UIElementIdentity, int>(capacity);
        _committedChanges = new UIElementChange[capacity];
        _committedSequences = new long[capacity];
        _committedRevisions = new long[capacity];
        _transactionChanges = new UIElementChange[capacity];
        _transactionIndices = new Dictionary<UIElementIdentity, int>(capacity);
    }

    /// <summary>Gets the maximum number of distinct pending elements.</summary>
    public int Capacity => _changes.Length;

    /// <summary>Gets the number of changes that fit in the journal.</summary>
    public int PendingCount
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Gets the latest committed change revision.</summary>
    public long Revision
    {
        get { lock (_gate) return _revision; }
    }

    /// <summary>Gets whether a complete UI rebuild is needed after an overflow.</summary>
    public bool RequiresFullResync
    {
        get { lock (_gate) return _requiresFullResync; }
    }

    /// <summary>Gets the starting position for this journal's publication reader.</summary>
    public UICommittedChangeCursor InitialCommittedCursor => new(_journalId, 0, 0);

    /// <summary>Gets one committed position only when no transaction is open.</summary>
    public bool TryGetCommittedCursor(out UICommittedChangeCursor cursor)
    {
        lock (_gate)
        {
            cursor = new(_journalId, _committedSequence, _revision);
            return _transactionDepth == 0;
        }
    }

    /// <summary>Copies complete committed batches without consuming layout changes.</summary>
    public bool TryCopyCommittedSince(UICommittedChangeCursor cursor,
        Span<UIElementChange> destination, out UICommittedChangeRead read)
    {
        lock (_gate)
        {
            UICommittedChangeCursor end = new(_journalId, _committedSequence, _revision);
            if (cursor.JournalId != _journalId || cursor.Sequence < 0 ||
                cursor.Sequence > _committedSequence || cursor.Revision < 0 ||
                cursor.Revision > _revision ||
                cursor.Sequence == _committedSequence && cursor.Revision != _revision)
            {
                read = new(UICommittedChangeReadStatus.InvalidCursor, end, 0,
                    UIChangeDomain.None);
                return false;
            }
            if (_transactionDepth != 0)
            {
                read = new(UICommittedChangeReadStatus.Busy, end, 0,
                    UIChangeDomain.None);
                return false;
            }
            if (cursor == end)
            {
                read = new(UICommittedChangeReadStatus.Complete, end, 0,
                    UIChangeDomain.None);
                return true;
            }
            if (cursor.Sequence < _oldestReadableSequence)
            {
                read = new(UICommittedChangeReadStatus.FullResync, end, 0,
                    UIChangeDomain.All);
                return false;
            }
            if (!MatchesCommittedRevision(cursor))
            {
                read = new(UICommittedChangeReadStatus.InvalidCursor, end, 0,
                    UIChangeDomain.None);
                return false;
            }

            int count = 0;
            UIChangeDomain domains = UIChangeDomain.None;
            for (int index = 0; index < _committedCount; index++)
            {
                int slot = (_committedStart + index) % Capacity;
                if (_committedSequences[slot] <= cursor.Sequence)
                    continue;
                count++;
                domains |= _committedChanges[slot].Domains;
            }
            if (destination.Length < count)
            {
                read = new(UICommittedChangeReadStatus.NeedCapacity, end, count, domains);
                return false;
            }
            int copied = 0;
            for (int index = 0; index < _committedCount; index++)
            {
                int slot = (_committedStart + index) % Capacity;
                if (_committedSequences[slot] > cursor.Sequence)
                    destination[copied++] = _committedChanges[slot];
            }
            read = new(UICommittedChangeReadStatus.Complete, end, count, domains);
            return true;
        }
    }

    /// <summary>
    /// Checks whether this thread can change the live UI tree while a transaction is active.
    /// Existing update and collection phases must still serialize live tree writes.
    /// </summary>
    public void VerifyMutationAccess()
    {
        lock (_gate)
            VerifyTransactionOwner();
    }

    /// <summary>
    /// Starts a nestable transaction on this thread. The scope allocates once here;
    /// recording and draining within the preallocated capacity do not allocate.
    /// </summary>
    public IDisposable BeginTransaction()
    {
        lock (_gate)
        {
            VerifyTransactionOwner();
            int threadId = Thread.CurrentThread.ManagedThreadId;
            UIChangeTransactionScope scope = new(this, threadId)
            {
                Previous = _topScope,
            };
            _topScope = scope;
            _transactionOwnerThreadId = threadId;
            _transactionDepth++;
            return scope;
        }
    }

    /// <summary>Combines a change by full element identity and keeps its newest parent.</summary>
    public void Record(UIElementIdentity element, UIElementIdentity parent, UIChangeDomain domains)
        => RecordCore(element, parent, domains, false);

    /// <summary>Records a tombstone for an attachment that must never be reused.</summary>
    public void RecordRemoval(UIElementIdentity element)
        => RecordCore(element, default, UIChangeDomain.All, true);

    private void RecordCore(UIElementIdentity element, UIElementIdentity parent, UIChangeDomain domains, bool removed)
    {
        lock (_gate)
        {
            VerifyTransactionOwner();
            if (!element.IsValid)
                throw new ArgumentException("The element identity must be valid.", nameof(element));
            if ((domains & ~UIChangeDomain.All) != UIChangeDomain.None)
                throw new ArgumentOutOfRangeException(nameof(domains));
            if (domains == UIChangeDomain.None)
                return;
            if (_revision == long.MaxValue || _committedSequence == long.MaxValue)
                throw new InvalidOperationException("The UI change revision is exhausted.");

            if (_indices.TryGetValue(element, out int index))
            {
                UIElementChange previous = _changes[index];
                _changes[index] = new UIElementChange(element, parent, previous.Domains | domains, previous.IsRemoved || removed);
            }
            else if (_count < Capacity)
            {
                _changes[_count] = new UIElementChange(element, parent, domains, removed);
                _indices.Add(element, _count);
                _count++;
            }
            else
            {
                _requiresFullResync = true;
            }

            _domains |= domains;
            if (_transactionDepth > 0)
            {
                if (!_transactionChangesOverflowed)
                {
                    if (_transactionIndices.TryGetValue(element, out int transactionIndex))
                    {
                        UIElementChange previous = _transactionChanges[transactionIndex];
                        _transactionChanges[transactionIndex] = new UIElementChange(element,
                            parent, previous.Domains | domains, previous.IsRemoved || removed);
                    }
                    else if (_transactionChangeCount < Capacity)
                    {
                        _transactionChanges[_transactionChangeCount] =
                            new UIElementChange(element, parent, domains, removed);
                        _transactionIndices.Add(element, _transactionChangeCount++);
                    }
                    else
                        _transactionChangesOverflowed = true;
                }
                _transactionDirty = true;
            }
            else
            {
                _revision++;
                _committedSequence++;
                AppendCommitted(new UIElementChange(element, parent, domains, removed),
                    _committedSequence);
            }
        }
    }

    private void AppendCommitted(UIElementChange change, long sequence)
    {
        ReserveCommitted(1);
        int slot = (_committedStart + _committedCount) % Capacity;
        _committedChanges[slot] = change;
        _committedSequences[slot] = sequence;
        _committedRevisions[slot] = _revision;
        _committedCount++;
    }

    private bool MatchesCommittedRevision(UICommittedChangeCursor cursor)
    {
        if (cursor.Sequence == _oldestReadableSequence)
            return cursor.Revision == _oldestReadableRevision;
        for (int index = 0; index < _committedCount; index++)
        {
            int slot = (_committedStart + index) % Capacity;
            if (_committedSequences[slot] == cursor.Sequence)
                return cursor.Revision == _committedRevisions[slot];
        }
        return cursor.Sequence == _committedSequence && cursor.Revision == _revision;
    }

    private void ReserveCommitted(int count)
    {
        while (count > Capacity - _committedCount)
        {
            long removedSequence = _committedSequences[_committedStart];
            long removedRevision = _committedRevisions[_committedStart];
            do
            {
                _committedStart = (_committedStart + 1) % Capacity;
                _committedCount--;
            } while (_committedCount > 0 &&
                     _committedSequences[_committedStart] == removedSequence);
            _oldestReadableSequence = removedSequence;
            _oldestReadableRevision = removedRevision;
        }
    }

    private void RequireCommittedResync(long sequence)
    {
        _committedStart = 0;
        _committedCount = 0;
        _oldestReadableSequence = sequence;
        _oldestReadableRevision = _revision;
    }

    /// <summary>
    /// Copies pending changes and clears them. Returns false during a transaction or
    /// when the destination is too short. In both cases, batch describes pending work.
    /// </summary>
    public bool TryDrain(Span<UIElementChange> destination, out UIChangeBatch batch)
    {
        lock (_gate)
        {
            batch = new UIChangeBatch(_revision, _count, _domains, _requiresFullResync);
            if (_transactionDepth > 0 || destination.Length < _count)
                return false;

            _changes.AsSpan(0, _count).CopyTo(destination);
            _changes.AsSpan(0, _count).Clear();
            _indices.Clear();
            _count = 0;
            _domains = UIChangeDomain.None;
            _requiresFullResync = false;
            return true;
        }
    }

    internal void EndTransaction(UIChangeTransactionScope scope)
    {
        lock (_gate)
        {
            if (Thread.CurrentThread.ManagedThreadId != scope.OwnerThreadId)
                throw new InvalidOperationException("A UI change transaction must end on its owner thread.");
            if (scope.IsDisposed)
                return;
            if (!ReferenceEquals(_topScope, scope))
                throw new InvalidOperationException("UI change transaction scopes must end in reverse order.");

            scope.IsDisposed = true;
            _topScope = scope.Previous;
            scope.Previous = null;
            _transactionDepth--;
            if (_transactionDepth != 0)
                return;

            _transactionOwnerThreadId = 0;
            if (_transactionDirty)
            {
                _revision++;
                _committedSequence++;
                if (_transactionChangesOverflowed)
                    RequireCommittedResync(_committedSequence);
                else
                {
                    ReserveCommitted(_transactionChangeCount);
                    for (int index = 0; index < _transactionChangeCount; index++)
                    {
                        int slot = (_committedStart + _committedCount) % Capacity;
                        _committedChanges[slot] = _transactionChanges[index];
                        _committedSequences[slot] = _committedSequence;
                        _committedRevisions[slot] = _revision;
                        _committedCount++;
                    }
                }
                _transactionChanges.AsSpan(0, _transactionChangeCount).Clear();
                _transactionIndices.Clear();
                _transactionChangeCount = 0;
                _transactionChangesOverflowed = false;
                _transactionDirty = false;
            }
        }
    }

    private void VerifyTransactionOwner()
    {
        if (_transactionDepth > 0 && _transactionOwnerThreadId != Thread.CurrentThread.ManagedThreadId)
            throw new InvalidOperationException("A UI change transaction is active on another thread.");
    }
}

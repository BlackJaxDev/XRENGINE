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
    private readonly object _gate = new();
    private readonly UIElementChange[] _changes;
    private readonly Dictionary<UIElementIdentity, int> _indices;
    private int _count;
    private long _revision;
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
            if (_revision == long.MaxValue)
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
                _transactionDirty = true;
            else
                _revision++;
        }
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

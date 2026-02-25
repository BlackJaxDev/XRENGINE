using System.Diagnostics;
using XREngine.Core;
using XREngine.Data.Core;

namespace System.Collections.Generic
{
    /// <summary>
    /// Provides a readonly interface for an <see cref="EventList{T}"/> to subscribe to events.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IEventListReadOnly<T> : IEnumerable<T>, IEnumerable, ICollection, IReadOnlyList<T>, IReadOnlyCollection<T>
    {
        new int Count { get; }
        event EventList<T>.SingleCancelableHandler PreAnythingAdded;
        event EventList<T>.SingleHandler PostAnythingAdded;
        event EventList<T>.SingleCancelableHandler PreAnythingRemoved;
        event EventList<T>.SingleHandler PostAnythingRemoved;
        event EventList<T>.SingleCancelableHandler PreAdded;
        event EventList<T>.SingleHandler PostAdded;
        event EventList<T>.MultiCancelableHandler PreAddedRange;
        event EventList<T>.MultiHandler PostAddedRange;
        event EventList<T>.SingleCancelableHandler PreRemoved;
        event EventList<T>.SingleHandler PostRemoved;
        event EventList<T>.MultiCancelableHandler PreRemovedRange;
        event EventList<T>.MultiHandler PostRemovedRange;
        event EventList<T>.SingleCancelableInsertHandler PreInserted;
        event EventList<T>.SingleInsertHandler PostInserted;
        event EventList<T>.MultiCancelableInsertHandler PreInsertedRange;
        event EventList<T>.MultiInsertHandler PostInsertedRange;
        event Func<bool> PreModified;
        event Action PostModified;
        event EventList<T>.PreIndexSetHandler PreIndexSet;
        event EventList<T>.PostIndexSetHandler PostIndexSet;
        event TCollectionChangedEventHandler<T> CollectionChanged;
        new T this[int index] { get; }
    }

    [Serializable]
    /// <summary>
    /// A derivation of <see cref="ThreadSafeList{T}"/> that monitors all operations and provides events for each kind of operation.
    /// </summary>
    public partial class EventList<T> : XRObjectBase, IEventListReadOnly<T>, IList<T>, ICollection<T>, IEnumerable<T>, IEnumerable, IList, ICollection, IReadOnlyList<T>, IReadOnlyCollection<T>
    {
        private readonly List<T> _list;
        private ReaderWriterLockSlim? _lock;

        public bool ThreadSafe
        {
            get => _lock != null;
            set => _lock = value ? new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion) : null;
        }

        public delegate void SingleHandler(T item);
        public delegate bool SingleCancelableHandler(T item);

        public delegate void MultiHandler(IEnumerable<T> items);
        public delegate bool MultiCancelableHandler(IEnumerable<T> items);

        public delegate void SingleInsertHandler(T item, int index);
        public delegate bool SingleCancelableInsertHandler(T item, int index);

        public delegate void MultiInsertHandler(IEnumerable<T> items, int index);
        public delegate bool MultiCancelableInsertHandler(IEnumerable<T> items, int index);

        public delegate bool PreIndexSetHandler(int index, T newItem);
        public delegate void PostIndexSetHandler(int index, T prevItem);

        /// <summary>
        /// Event called for every individual item just before being added to the list.
        /// </summary>
        public event SingleCancelableHandler? PreAnythingAdded;
        /// <summary>
        /// Event called for every individual item after being added to the list.
        /// </summary>
        public event SingleHandler? PostAnythingAdded;
        /// <summary>
        /// Event called for every individual item just before being removed from the list.
        /// </summary>
        public event SingleCancelableHandler? PreAnythingRemoved;
        /// <summary>
        /// Event called for every individual item after being removed from the list.
        /// </summary>
        public event SingleHandler? PostAnythingRemoved;
        /// <summary>
        /// Event called before an item is added using the Add method.
        /// </summary>
        public event SingleCancelableHandler? PreAdded;
        /// <summary>
        /// Event called after an item is added using the Add method.
        /// </summary>
        public event SingleHandler? PostAdded;
        /// <summary>
        /// Event called before an item is added using the AddRange method.
        /// </summary>
        public event MultiCancelableHandler? PreAddedRange;
        /// <summary>
        /// Event called after an item is added using the AddRange method.
        /// </summary>
        public event MultiHandler? PostAddedRange;
        /// <summary>
        /// Event called before an item is removed using the Remove method.
        /// </summary>
        public event SingleCancelableHandler? PreRemoved;
        /// <summary>
        /// Event called after an item is removed using the Remove method.
        /// </summary>
        public event SingleHandler? PostRemoved;
        /// <summary>
        /// Event called before an item is removed using the RemoveRange method.
        /// </summary>
        public event MultiCancelableHandler? PreRemovedRange;
        /// <summary>
        /// Event called after an item is removed using the RemoveRange method.
        /// </summary>
        public event MultiHandler? PostRemovedRange;
        /// <summary>
        /// Event called before an item is inserted using the Insert method.
        /// </summary>
        public event SingleCancelableInsertHandler? PreInserted;
        /// <summary>
        /// Event called after an item is removed using the Insert method.
        /// </summary>
        public event SingleInsertHandler? PostInserted;
        /// <summary>
        /// Event called before an item is inserted using the InsertRange method.
        /// </summary>
        public event MultiCancelableInsertHandler? PreInsertedRange;
        /// <summary>
        /// Event called after an item is inserted using the InsertRange method.
        /// </summary>
        public event MultiInsertHandler? PostInsertedRange;
        /// <summary>
        /// Event called before this list is modified in any way at all.
        /// </summary>
        public event Func<bool>? PreModified;
        /// <summary>
        /// Event called after this list is modified in any way at all.
        /// </summary>
        public event Action? PostModified;

        public event PreIndexSetHandler? PreIndexSet;
        public event PostIndexSetHandler? PostIndexSet;

        public event TCollectionChangedEventHandler<T>? CollectionChanged;

        public bool _updating = false;
        public bool _allowDuplicates = true;
        public bool _allowNull = true;

        public bool AllowDuplicates
        {
            get => _allowDuplicates;
            set => SetField(ref _allowDuplicates, value);
        }
        public bool AllowNull
        {
            get => _allowNull;
            set => SetField(ref _allowNull, value);
        }

        public EventList()
        {
            _list = [];
        }
        public EventList(bool allowDuplicates, bool allowNull)
        {
            _list = [];
            _allowDuplicates = allowDuplicates;
            _allowNull = allowNull;
        }
        public EventList(IEnumerable<T> list)
        {
            _list = [];
            AddRange(list);
        }
        public EventList(IEnumerable<T> list, bool allowDuplicates, bool allowNull)
            : this(allowDuplicates, allowNull) => AddRange(list);
        public EventList(int capacity)
        {
            _list = new List<T>(capacity);
        }

        /// <summary>
        /// Completely replaces the list's items with the given items.
        /// </summary>
        /// <param name="items">The items to set as the collection.</param>
        /// <param name="reportRemoved">If true, notifies subscribers that previous items were removed.</param>
        /// <param name="reportAdded">If true, notifies subscribers that new items have been added.</param>
        /// <param name="reportModified">If true, notifies subscribers that the list has changed.</param>
        public void Set(IEnumerable<T> items, bool reportRemoved = true, bool reportAdded = true, bool reportModified = true)
        {
            Clear(reportRemoved, false);
            AddRange(items, reportAdded, reportModified);
        }

        public bool Add(T item) => Add(item, true, true);
        public bool Add(T item, bool reportAdded, bool reportModified)
        {
            if (!_allowNull && item == null)
                return false;

            if (!_allowDuplicates && Contains(item))
                return false;

            if (!_updating)
            {
                if (reportAdded)
                {
                    if (!(PreAdded?.Invoke(item) ?? true))
                        return false;

                    if (!(PreAnythingAdded?.Invoke(item) ?? true))
                        return false;
                }
                if (reportModified)
                {
                    if (!(PreModified?.Invoke() ?? true))
                        return false;
                }
            }

            try
            {
                _lock?.EnterWriteLock();
                _list.Add(item);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }

            if (!_updating)
            {
                if (reportAdded)
                {
                    PostAdded?.Invoke(item);
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

        public bool Contains(T item)
        {
            try
            {
                _lock?.EnterReadLock();
                return _list.Contains(item);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
                return false;
            }
            finally
            {
                _lock?.ExitReadLock();
            }
        }
        public void AddRange(IEnumerable<T> collection) => AddRange(collection, true, true);
        public void AddRange(IEnumerable<T> collection, bool reportAddedRange, bool reportModified)
        {
            if (collection is null)
                return;

            List<T> items = PrepareItemsForInsertion(collection);
            
            if (items.Count == 0)
                return;
            
            collection = items;

            if (!_updating)
            {
                if (reportModified)
                {
                    if (!(PreModified?.Invoke() ?? true))
                        return;
                }
                if (reportAddedRange)
                {
                    if (!(PreAddedRange?.Invoke(collection) ?? true))
                        return;

                    if (PreAnythingAdded != null)
                    {
                        // Filter items that are rejected by PreAnythingAdded, but materialize immediately
                        var filteredItems = new List<T>();
                        foreach (T item in items)
                            if (PreAnythingAdded(item))
                                filteredItems.Add(item);
                        items = filteredItems;
                        collection = items;
                        if (items.Count == 0)
                            return;
                    }
                }
            }

            try
            {
                _lock?.EnterWriteLock();
                _list.AddRange(collection);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }

            if (!_updating)
            {
                if (reportAddedRange)
                {
                    PostAddedRange?.Invoke(collection);
                    if (PostAnythingAdded != null)
                        foreach (T item in collection)
                            PostAnythingAdded(item);
                }
                if (reportModified)
                {
                    PostModified?.Invoke();
                    CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Add, collection.ToList()));
                }
            }
        }
        public bool Remove(T item) => Remove(item, true, true);
        public bool Remove(T item, bool reportRemoved, bool reportModified)
        {
            if (!_updating)
            {
                if (reportModified)
                {
                    if (!(PreModified?.Invoke() ?? true))
                        return false;
                }
                if (reportRemoved)
                {
                    if (!(PreRemoved?.Invoke(item) ?? true))
                        return false;

                    if (!(PreAnythingRemoved?.Invoke(item) ?? true))
                        return false;
                }
            }

            bool success;
            try
            {
                _lock?.EnterWriteLock();
                success = _list.Remove(item);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
                success = false;
            }
            finally
            {
                _lock?.ExitWriteLock();
            }

            if (success)
            {
                if (!_updating)
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
            return false;
        }
        public void RemoveRange(int index, int count) => RemoveRange(index, count, true, true);
        public void RemoveRange(int index, int count, bool reportRemovedRange, bool reportModified)
        {
            List<IndexedItem> approvedRemovals = [];
            List<T> removedItems = [];

            if (!_updating && reportRemovedRange)
            {
                approvedRemovals = SnapshotRangeIndexed(index, count);
                removedItems = approvedRemovals.Select(x => x.Item).ToList();
            }

            if (!_updating)
            {
                if (reportModified)
                {
                    if (!(PreModified?.Invoke() ?? true))
                        return;
                }
                if (reportRemovedRange)
                {
                    if (!(PreRemovedRange?.Invoke(removedItems) ?? true))
                        return;

                    if (PreAnythingRemoved != null && approvedRemovals.Count > 0)
                    {
                        approvedRemovals = FilterApprovedRemovals(approvedRemovals);
                        removedItems = approvedRemovals.Select(x => x.Item).ToList();
                    }
                }
            }

            try
            {
                _lock?.EnterWriteLock();
                if (_updating || !reportRemovedRange)
                {
                    _list.RemoveRange(index, count);
                }
                else if (approvedRemovals.Count == count)
                {
                    _list.RemoveRange(index, count);
                }
                else
                {
                    for (int i = approvedRemovals.Count - 1; i >= 0; --i)
                        _list.RemoveAt(approvedRemovals[i].Index);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }

            if (!_updating)
            {
                if (reportRemovedRange)
                {
                    PostRemovedRange?.Invoke(removedItems);
                    if (PostAnythingRemoved != null)
                        foreach (T item in removedItems)
                            PostAnythingRemoved(item);
                }
                if (reportModified)
                {
                    PostModified?.Invoke();
                    CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Remove, removedItems));
                }
            }

        }

        public IEnumerable<T> GetRange(int index, int count)
        {
            try
            {
                _lock?.EnterReadLock();
                return _list.GetRange(index, count);
            }
            finally
            {
                _lock?.ExitReadLock();
            }
        }

        public void RemoveAt(int index) => RemoveAt(index, true, true);
        public void RemoveAt(int index, bool reportRemoved, bool reportModified)
        {
            T item = this[index];

            if (!_updating)
            {
                if (reportModified)
                {
                    if (!(PreModified?.Invoke() ?? true))
                        return;
                }
                if (reportRemoved)
                {
                    if (!(PreRemoved?.Invoke(item) ?? true))
                        return;

                    if (!(PreAnythingRemoved?.Invoke(item) ?? true))
                        return;
                }
            }

            try
            {
                _lock?.EnterWriteLock();
                _list.RemoveAt(index);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }

            if (!_updating)
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
        }
        public void Clear() => Clear(true, true);
        public void Clear(bool reportRemovedRange, bool reportModified)
        {
            List<IndexedItem> approvedRemovals = [];
            List<T> removedItems = [];

            if (reportRemovedRange)
            {
                try
                {
                    _lock?.EnterReadLock();
                    if (_list.Count > 0)
                    {
                        approvedRemovals = new List<IndexedItem>(_list.Count);
                        for (int i = 0; i < _list.Count; ++i)
                            approvedRemovals.Add(new IndexedItem(i, _list[i]));
                        removedItems = approvedRemovals.Select(x => x.Item).ToList();
                    }
                }
                finally
                {
                    _lock?.ExitReadLock();
                }
            }

            if (!_updating)
            {
                if (reportModified)
                {
                    if (!(PreModified?.Invoke() ?? true))
                        return;
                }
                if (reportRemovedRange)
                {
                    if (!(PreRemovedRange?.Invoke(removedItems) ?? true))
                        return;

                    if (PreAnythingRemoved != null && approvedRemovals.Count > 0)
                    {
                        approvedRemovals = FilterApprovedRemovals(approvedRemovals);
                        removedItems = approvedRemovals.Select(x => x.Item).ToList();
                    }
                }
            }

            try
            {
                _lock?.EnterWriteLock();
                if (_updating || !reportRemovedRange)
                {
                    _list.Clear();
                }
                else if (approvedRemovals.Count == _list.Count)
                {
                    _list.Clear();
                }
                else
                {
                    for (int i = approvedRemovals.Count - 1; i >= 0; --i)
                        _list.RemoveAt(approvedRemovals[i].Index);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }

            if (!_updating)
            {
                if (reportRemovedRange)
                {
                    PostRemovedRange?.Invoke(removedItems);
                    if (PostAnythingRemoved != null)
                        foreach (T item in removedItems)
                            PostAnythingRemoved(item);
                }
                if (reportModified)
                {
                    PostModified?.Invoke();
                    CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Clear));
                }
            }
        }
        public void RemoveAll(Predicate<T> match) => RemoveAll(match, true, true);
        public void RemoveAll(Predicate<T> match, bool reportRemovedRange, bool reportModified)
        {
            List<IndexedItem> approvedRemovals = [];
            List<T> removedItems = [];

            if (!_updating)
            {
                if (reportRemovedRange)
                {
                    approvedRemovals = SnapshotMatchesIndexed(match);
                    removedItems = approvedRemovals.Select(x => x.Item).ToList();
                }

                if (!_updating)
                {
                    if (reportModified)
                    {
                        if (!(PreModified?.Invoke() ?? true))
                            return;
                    }
                    if (reportRemovedRange)
                    {
                        if (!(PreRemovedRange?.Invoke(removedItems) ?? true))
                            return;

                        if (PreAnythingRemoved != null && approvedRemovals.Count > 0)
                        {
                            approvedRemovals = FilterApprovedRemovals(approvedRemovals);
                            removedItems = approvedRemovals.Select(x => x.Item).ToList();
                        }
                    }
                }
            }

            try
            {
                _lock?.EnterWriteLock();
                if (_updating || !reportRemovedRange)
                {
                    _list.RemoveAll(match);
                }
                else
                {
                    for (int i = approvedRemovals.Count - 1; i >= 0; --i)
                        _list.RemoveAt(approvedRemovals[i].Index);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }

            if (!_updating)
            {
                if (reportRemovedRange)
                {
                    PostRemovedRange?.Invoke(removedItems);
                    if (PostAnythingRemoved != null)
                        foreach (T item in removedItems)
                            PostAnythingRemoved(item);
                }
                if (reportModified)
                {
                    PostModified?.Invoke();
                    CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Remove, removedItems));
                }
            }
        }

        public IEnumerable<T> FindAll(Predicate<T> match)
        {
            try
            {
                _lock?.EnterReadLock();
                return _list.FindAll(match);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
                return [];
            }
            finally
            {
                _lock?.ExitReadLock();
            }
        }

        public void Insert(int index, T item) => Insert(index, item, true, true);
        public void Insert(int index, T item, bool reportInserted, bool reportModified)
        {
            if (!_allowNull && item == null)
                return;

            if (!_allowDuplicates && Contains(item))
                return;

            if (!_updating)
            {
                if (reportModified)
                {
                    if (!(PreModified?.Invoke() ?? true))
                        return;
                }
                if (reportInserted)
                {
                    if (!(PreInserted?.Invoke(item, index) ?? true))
                        return;

                    if (!(PreAnythingAdded?.Invoke(item) ?? true))
                        return;
                }
            }

            try
            {
                _lock?.EnterWriteLock();
                _list.Insert(index, item);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }

            if (!_updating)
            {
                if (reportInserted)
                {
                    PostInserted?.Invoke(item, index);
                    PostAnythingAdded?.Invoke(item);
                }
                if (reportModified)
                {
                    PostModified?.Invoke();
                    CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Add, item));
                }
            }
        }
        public void InsertRange(int index, IEnumerable<T> collection) => InsertRange(index, collection, true, true);
        public void InsertRange(int index, IEnumerable<T> collection, bool reportInsertedRange, bool reportModified)
        {
            if (collection is null)
                return;

            List<T> items = PrepareItemsForInsertion(collection);
            
            if (items.Count == 0)
                return;
            
            collection = items;

            if (!_updating)
            {
                if (reportModified)
                {
                    if (!(PreModified?.Invoke() ?? true))
                        return;
                }
                if (reportInsertedRange)
                {
                    if (!(PreInsertedRange?.Invoke(collection, index) ?? true))
                        return;

                    if (PreAnythingAdded != null)
                    {
                        // Filter items that are rejected by PreAnythingAdded, but materialize immediately
                        var filteredItems = new List<T>();
                        foreach (T item in items)
                            if (PreAnythingAdded(item))
                                filteredItems.Add(item);
                        items = filteredItems;
                        collection = items;
                        if (items.Count == 0)
                            return;
                    }
                }
            }

            try
            {
                _lock?.EnterWriteLock();
                _list.InsertRange(index, collection);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }

            if (!_updating)
            {
                if (reportInsertedRange)
                {
                    PostInsertedRange?.Invoke(collection, index);
                    if (PostAnythingAdded != null)
                        foreach (T item in collection)
                            PostAnythingAdded(item);
                }
                if (reportModified)
                {
                    PostModified?.Invoke();
                    CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Add, collection.ToList()));
                }
            }
        }
        public void Reverse(int index, int count) => Reverse(index, count, true);
        public void Reverse(int index, int count, bool reportModified)
        {
            bool report = reportModified && !_updating;
            if (report)
            {
                if (!(PreModified?.Invoke() ?? true))
                    return;
            }
            try
            {
                _lock?.EnterWriteLock();
                _list.Reverse(index, count);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }
            if (report)
            {
                PostModified?.Invoke();
            }
        }
        public void Reverse() => Reverse(true);
        public void Reverse(bool reportModified)
        {
            bool report = reportModified && !_updating;
            if (report)
            {
                if (!(PreModified?.Invoke() ?? true))
                    return;
            }
            try
            {
                _lock?.EnterWriteLock();
                _list.Reverse();
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }
            if (report)
            {
                PostModified?.Invoke();
            }
        }
        public void Sort(int index, int count, IComparer<T> comparer) => Sort(index, count, comparer, true);
        public void Sort(int index, int count, IComparer<T> comparer, bool reportModified)
        {
            bool report = reportModified && !_updating;
            if (report)
            {
                if (!(PreModified?.Invoke() ?? true))
                    return;
            }
            try
            {
                _lock?.EnterWriteLock();
                _list.Sort(index, count, comparer);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }
            if (report)
            {
                PostModified?.Invoke();
            }
        }
        public void Sort() => Sort(true);
        public void Sort(bool reportModified)
        {
            bool report = reportModified && !_updating;
            if (report)
            {
                if (!(PreModified?.Invoke() ?? true))
                    return;
            }
            try
            {
                _lock?.EnterWriteLock();
                _list.Sort();
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }
            if (report)
            {
                PostModified?.Invoke();
            }
        }
        public void Sort(IComparer<T> comparer) => Sort(comparer, true);
        public void Sort(IComparer<T> comparer, bool reportModified)
        {
            bool report = reportModified && !_updating;
            if (report)
            {
                if (!(PreModified?.Invoke() ?? true))
                    return;
            }
            try
            {
                _lock?.EnterWriteLock();
                _list.Sort(comparer);
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            finally
            {
                _lock?.ExitWriteLock();
            }
            if (report)
            {
                PostModified?.Invoke();
            }
        }
        public T this[int index]
        {
            get
            {
                try
                {
                    _lock?.EnterReadLock();
                    return _list[index];
                }
                catch (Exception ex)
                {
                    Trace.TraceError(ex.ToString());
                    return default!;
                }
                finally
                {
                    _lock?.ExitReadLock();
                }
            }
            set
            {
                if (!_allowNull && value == null)
                    return;
                if (!_allowDuplicates && Contains(value))
                    return;
                if (!_updating)
                {
                    if (!(PreModified?.Invoke() ?? true))
                        return;
                    if (!(PreAdded?.Invoke(value) ?? true))
                        return;
                    if (!(PreAnythingAdded?.Invoke(value) ?? true))
                        return;
                    if (!(PreIndexSet?.Invoke(index, value) ?? true))
                        return;
                }
                T prev = default!;
                try
                {
                    _lock?.EnterWriteLock();
                    prev = _list[index];
                    _list[index] = value;
                }
                catch (Exception ex)
                {
                    Trace.TraceError(ex.ToString());
                }
                finally
                {
                    _lock?.ExitWriteLock();
                }
                if (!_updating)
                {
                    PostAdded?.Invoke(value);
                    PostAnythingAdded?.Invoke(value);
                    PostIndexSet?.Invoke(index, prev);
                    PostModified?.Invoke();
                    CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Replace, value, index));
                }
            }
        }

        int ICollection.Count => Count;
        object ICollection.SyncRoot => ((ICollection)_list).SyncRoot;
        bool ICollection.IsSynchronized => ((ICollection)_list).IsSynchronized;
        int IReadOnlyCollection<T>.Count => Count;

        public int Count
        {
            get
            {
                try
                {
                    _lock?.EnterReadLock();
                    return _list.Count;
                }
                finally
                {
                    _lock?.ExitReadLock();
                }
            }
        }

        public bool IsReadOnly
            => ((ICollection<T>)_list).IsReadOnly;

        public bool IsFixedSize
            => ((IList)_list).IsFixedSize;

        object? IList.this[int index]
        {
            get => ((IList)_list)[index];
            set => this[index] = CastObjectValue(value);
        }

        T IReadOnlyList<T>.this[int index]
            => this[index];

        public int IndexOf(T value)
        {
            try
            {
                _lock?.EnterReadLock();
                return _list.IndexOf(value);
            }
            finally
            {
                _lock?.ExitReadLock();
            }
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            try
            {
                _lock?.EnterReadLock();
                _list.CopyTo(array, arrayIndex);
            }
            finally
            {
                _lock?.ExitReadLock();
            }
        }

        void ICollection.CopyTo(Array array, int index)
            => CopyTo((T[])array, index);

        public IEnumerator<T> GetEnumerator()
            => ThreadSafe ? new ThreadSafeListEnumerator<T>(_list, _lock) : _list.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();

        void ICollection<T>.Add(T item)
            => Add(item);

        public int Add(object? value)
        {
            T item = CastObjectValue(value);
            if (!Add(item))
                return -1;
            return IndexOf(item);
        }

        public bool Contains(object? value)
        {
            if (value is null)
                return default(T) is null && Contains(default!);

            return value is T item && Contains(item);
        }

        public int IndexOf(object? value)
        {
            if (value is null)
                return default(T) is null ? IndexOf(default!) : -1;

            return value is T item ? IndexOf(item) : -1;
        }

        public void Insert(int index, object? value)
            => Insert(index, CastObjectValue(value));

        public void Remove(object? value)
        {
            if (value is null)
            {
                if (default(T) is null)
                    Remove(default!);
                return;
            }

            if (value is T item)
                Remove(item);
        }

        private static T CastObjectValue(object? value)
        {
            if (value is null)
            {
                if (default(T) is null)
                    return default!;

                throw new ArgumentNullException(nameof(value));
            }

            if (value is T item)
                return item;

            throw new ArgumentException($"Value must be of type {typeof(T).FullName}.", nameof(value));
        }

        private readonly struct IndexedItem(int index, T item)
        {
            public int Index { get; } = index;
            public T Item { get; } = item;
        }

        private List<T> PrepareItemsForInsertion(IEnumerable<T> collection)
        {
            IEnumerable<T> source = _allowNull ? collection : collection.Where(item => item is not null);
            List<T> items = source.ToList();
            if (items.Count == 0 || _allowDuplicates)
                return items;

            HashSet<T> seen;
            try
            {
                _lock?.EnterReadLock();
                seen = [.. _list];
            }
            finally
            {
                _lock?.ExitReadLock();
            }

            var deduped = new List<T>(items.Count);
            foreach (T item in items)
            {
                if (seen.Add(item))
                    deduped.Add(item);
            }

            return deduped;
        }

        private List<IndexedItem> SnapshotRangeIndexed(int index, int count)
        {
            try
            {
                _lock?.EnterReadLock();
                var snapshot = new List<IndexedItem>(count);
                for (int i = 0; i < count; ++i)
                    snapshot.Add(new IndexedItem(index + i, _list[index + i]));
                return snapshot;
            }
            finally
            {
                _lock?.ExitReadLock();
            }
        }

        private List<IndexedItem> SnapshotMatchesIndexed(Predicate<T> match)
        {
            try
            {
                _lock?.EnterReadLock();
                var snapshot = new List<IndexedItem>();
                for (int i = 0; i < _list.Count; ++i)
                {
                    T item = _list[i];
                    if (match(item))
                        snapshot.Add(new IndexedItem(i, item));
                }
                return snapshot;
            }
            finally
            {
                _lock?.ExitReadLock();
            }
        }

        private List<IndexedItem> FilterApprovedRemovals(List<IndexedItem> candidates)
        {
            if (candidates.Count == 0 || PreAnythingRemoved is null)
                return candidates;

            var approved = new List<IndexedItem>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (PreAnythingRemoved(candidate.Item))
                    approved.Add(candidate);
            }
            return approved;
        }
    }
}

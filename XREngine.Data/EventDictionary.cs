using System.Collections;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;

namespace XREngine
{
    public interface IEventDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>, IReadOnlyEventDictionary<TKey, TValue> where TKey : notnull
    {
        //TValue this[TKey key] { get; set; }
        //void Add(TKey key, TValue value);
        //void Clear();
        //bool Remove(TKey key);
    }
    public interface IReadOnlyEventDictionary<TKey, TValue> :
        IReadOnlyDictionary<TKey, TValue>,
        IEnumerable<KeyValuePair<TKey, TValue>>,
        IEnumerable, IReadOnlyCollection<KeyValuePair<TKey, TValue>> where TKey : notnull
    {
        event EventDictionary<TKey, TValue>.DelAdded? Added;
        event EventDictionary<TKey, TValue>.DelCleared? Cleared;
        event EventDictionary<TKey, TValue>.DelRemoved? Removed;
        event EventDictionary<TKey, TValue>.DelSet? Set;
        event Action? Changed;
    }
    public class EventDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IEventDictionary<TKey, TValue> where TKey : notnull
    {
        public delegate void DelAdded(TKey key, TValue value);
        public delegate void DelCleared();
        public delegate void DelRemoved(TKey key, TValue value);
        public delegate void DelSet(TKey key, TValue oldValue, TValue newValue);

        public event DelAdded? Added;
        public event DelCleared? Cleared;
        public event DelRemoved? Removed;
        public event DelSet? Set;
        public event Action? Changed;

        public EventDictionary() : base() { }
        public EventDictionary(int capacity) : base(capacity) { }
        public EventDictionary(IEqualityComparer<TKey> comparer) : base(comparer) { }
        public EventDictionary(IDictionary<TKey, TValue> dictionary) : base(dictionary) { }
        public EventDictionary(int capacity, IEqualityComparer<TKey> comparer) : base(capacity, comparer) { }
        public EventDictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey> comparer) : base(dictionary, comparer) { }

        public new TValue this[TKey key]
        {
            get => base[key];
            set
            {
                if (base.TryGetValue(key, out TValue? old))
                {
                    base[key] = value;
                    Set?.Invoke(key, old, value);
                }
                else
                {
                    base[key] = value;
                    Added?.Invoke(key, value);
                }
                Changed?.Invoke();
            }
        }

        public new void Add(TKey key, TValue value)
        {
            base.Add(key, value);
            Added?.Invoke(key, value);
            Changed?.Invoke();
        }

        public new void Clear()
        {
            var items = this.ToArray();
            base.Clear();
            foreach (var item in items)
                Removed?.Invoke(item.Key, item.Value);
            Cleared?.Invoke();
            Changed?.Invoke();
        }

        public new bool Remove(TKey key)
        {
            if (!TryGetValue(key, out TValue? old))
                return false;
            bool success = base.Remove(key);
            if (success)
            {
                Removed?.Invoke(key, old);
                Changed?.Invoke();
            }
            return success;
        }

        /// <summary>
        /// Replaces the dictionary contents as one observable mutation. The complete new
        /// state is installed before any observer is called and <see cref="Changed"/> is
        /// raised at most once.
        /// </summary>
        /// <remarks>
        /// If an observer throws, the pre-replacement contents are restored before the
        /// exception escapes and the inverse per-entry notifications are dispatched
        /// best-effort so derived observers can converge on the restored state.
        /// </remarks>
        public void ReplaceContents(IEnumerable<KeyValuePair<TKey, TValue>> entries)
        {
            Dictionary<TKey, TValue> replacement = CreateReplacement(entries);
            KeyValuePair<TKey, TValue>[] previous = SnapshotEntries();
            if (SameContents(previous, replacement))
                return;

            ReplaceContentsWithoutNotifications(replacement);
            Exception? failure = NotifyReplacement(previous, replacement);
            if (failure is null)
                return;

            ReplaceContentsWithoutNotifications(previous);
            _ = NotifyReplacement(
                replacement.ToArray(),
                new Dictionary<TKey, TValue>(previous, Comparer));
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>
        /// Replaces the dictionary contents and retains the requested final state even when
        /// an observer throws. Every observer is still invoked, and the first observer failure
        /// is rethrown after the non-vetoable replacement completes.
        /// </summary>
        /// <remarks>
        /// This is intended for compensation and rollback paths where returning to the known
        /// safe state is more important than allowing an observer to veto the transition.
        /// </remarks>
        public void ReplaceContentsNonVetoable(IEnumerable<KeyValuePair<TKey, TValue>> entries)
        {
            Dictionary<TKey, TValue> replacement = CreateReplacement(entries);
            KeyValuePair<TKey, TValue>[] previous = SnapshotEntries();
            if (SameContents(previous, replacement))
                return;

            ReplaceContentsWithoutNotifications(replacement);
            Exception? failure = NotifyReplacement(previous, replacement);
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public KeyValuePair<TKey, TValue>[] SnapshotEntries()
        {
            var snapshot = new KeyValuePair<TKey, TValue>[base.Count];
            ((ICollection<KeyValuePair<TKey, TValue>>)(Dictionary<TKey, TValue>)this).CopyTo(snapshot, 0);
            return snapshot;
        }

        private void ReplaceContentsWithoutNotifications(IEnumerable<KeyValuePair<TKey, TValue>> entries)
        {
            base.Clear();
            foreach (KeyValuePair<TKey, TValue> entry in entries)
                base.Add(entry.Key, entry.Value);
        }

        private Dictionary<TKey, TValue> CreateReplacement(IEnumerable<KeyValuePair<TKey, TValue>> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);

            KeyValuePair<TKey, TValue>[] next = entries.ToArray();
            var replacement = new Dictionary<TKey, TValue>(next.Length, Comparer);
            foreach (KeyValuePair<TKey, TValue> entry in next)
                replacement.Add(entry.Key, entry.Value);
            return replacement;
        }

        private static bool SameContents(
            IReadOnlyList<KeyValuePair<TKey, TValue>> previous,
            IReadOnlyDictionary<TKey, TValue> replacement)
            => previous.Count == replacement.Count && previous.All(
                entry => replacement.TryGetValue(entry.Key, out TValue? value) &&
                    EqualityComparer<TValue>.Default.Equals(entry.Value, value));

        private static void InvokeAll(Delegate? handlers, Action<Delegate> invoke, ref Exception? failure)
        {
            if (handlers is null)
                return;

            foreach (Delegate handler in handlers.GetInvocationList())
                try
                {
                    invoke(handler);
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }
        }

        private Exception? NotifyReplacement(
            IReadOnlyList<KeyValuePair<TKey, TValue>> previous,
            IReadOnlyDictionary<TKey, TValue> current)
        {
            Exception? failure = null;
            foreach (KeyValuePair<TKey, TValue> entry in previous)
                if (!current.ContainsKey(entry.Key))
                    InvokeAll(Removed, handler => ((DelRemoved)handler)(entry.Key, entry.Value), ref failure);

            foreach (KeyValuePair<TKey, TValue> entry in previous)
                if (current.TryGetValue(entry.Key, out TValue? value) &&
                    !EqualityComparer<TValue>.Default.Equals(entry.Value, value))
                    InvokeAll(Set, handler => ((DelSet)handler)(entry.Key, entry.Value, value), ref failure);

            foreach (KeyValuePair<TKey, TValue> entry in current)
                if (!previous.Any(previousEntry => Comparer.Equals(previousEntry.Key, entry.Key)))
                    InvokeAll(Added, handler => ((DelAdded)handler)(entry.Key, entry.Value), ref failure);

            InvokeAll(Changed, handler => ((Action)handler)(), ref failure);
            return failure;
        }

        //object? IDictionary.this[object key]
        //{
        //    get => key is TKey k ? base[k] : (object?)null;
        //    set
        //    {
        //        if (key is TKey k && value is TValue v)
        //            this[k] = v;
        //    }
        //}
        TValue IDictionary<TKey, TValue>.this[TKey key]
        {
            get => this[key];
            set => this[key] = value;
        }

        void IDictionary<TKey, TValue>.Add(TKey key, TValue value)
            => Add(key, value);
        bool IDictionary<TKey, TValue>.Remove(TKey key)
            => Remove(key);
        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item)
            => Add(item);
        public void Add(KeyValuePair<TKey, TValue> item)
            => Add(item.Key, item.Value);
        void ICollection<KeyValuePair<TKey, TValue>>.Clear()
            => Clear();
        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
            => Remove(item);
        private bool Remove(KeyValuePair<TKey, TValue> item)
            => TryGetValue(item.Key, out TValue? value) && EqualityComparer<TValue>.Default.Equals(value, item.Value) && Remove(item.Key);
        //void IDictionary.Add(object key, object? value)
        //    => Add(key, value);
        private void Add(object key, object? value)
        {
            if (key is TKey k && value is TValue v)
                Add(k, v);
        }
        //void IDictionary.Clear()
        //    => Clear();
        //void IDictionary.Remove(object key)
        //    => Remove(key);
        private void Remove(object key)
        {
            if (key is TKey k)
                Remove(k);
        }
    }
}

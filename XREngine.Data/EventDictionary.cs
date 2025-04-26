using System.Collections;
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
                TValue old = base[key];
                base[key] = value;
                Set?.Invoke(key, old, value);
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
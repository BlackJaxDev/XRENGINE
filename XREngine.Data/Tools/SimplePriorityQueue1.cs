using System.Collections.Generic;

namespace XREngine.Data.Tools
{
    public class SimplePriorityQueue<T, TKey> where TKey : notnull, IComparable<TKey>
    {
        private readonly SortedDictionary<TKey, List<T>> _queue = new();

        public SimplePriorityQueue()
        {
        }

        public void Enqueue(T item, TKey priority)
        {
            if (!_queue.TryGetValue(priority, out List<T>? bucket))
            {
                bucket = new List<T>();
                _queue[priority] = bucket;
            }

            bucket.Add(item);
        }

        public T Dequeue()
        {
            if (_queue.Count == 0)
                throw new InvalidOperationException("The queue is empty.");

            using SortedDictionary<TKey, List<T>>.Enumerator enumerator = _queue.GetEnumerator();
            if (!enumerator.MoveNext())
                throw new InvalidOperationException("The queue is empty.");

            KeyValuePair<TKey, List<T>> first = enumerator.Current;
            List<T> bucket = first.Value;
            T item = bucket[0];
            bucket.RemoveAt(0);

            if (bucket.Count == 0)
                _queue.Remove(first.Key);

            return item;
        }

        public int Count()
        {
            int count = 0;
            foreach (List<T> itemList in _queue.Values)
            {
                count += itemList.Count;
            }
            return count;
        }
        public bool Contains(T item)
        {
            foreach (List<T> itemList in _queue.Values)
            {
                if (itemList.Contains(item))
                {
                    return true;
                }
            }
            return false;
        }

        public void Remove(T item)
        {
            bool found = false;
            TKey keyToRemove = default!;

            foreach (KeyValuePair<TKey, List<T>> entry in _queue)
            {
                if (!entry.Value.Remove(item))
                    continue;

                keyToRemove = entry.Key;
                found = true;
                break;
            }

            if (found && _queue[keyToRemove].Count == 0)
                _queue.Remove(keyToRemove);
        }

        public void UpdatePriority(T item, TKey newPriority)
        {
            Remove(item);
            Enqueue(item, newPriority);
        }
    }
}

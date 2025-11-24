using Extensions;
using System.Collections.ObjectModel;

namespace System.Collections.Generic
{
    public class ThreadSafeEnumerable<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _inner;
        private readonly ReaderWriterLockSlim _lock;

        public ThreadSafeEnumerable(IEnumerable<T> inner, ReaderWriterLockSlim rwlock)
        {
            _inner = inner;
            _lock = rwlock;
        }

        public IEnumerator<T> GetEnumerator()
            => new ThreadSafeEnumerator<T>(_inner.GetEnumerator(), _lock);

        IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();
    }
    [Serializable]
    public class ThreadSafeList<T> : List<T>
    {
        protected ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        //protected List<T> base;

        //public int Count => ((IList<T>)base).Count;
        //public bool IsReadOnly => ((IList<T>)base).IsReadOnly;
        //public bool IsFixedSize => ((IList)base).IsFixedSize;
        //public object SyncRoot => ((IList)base).SyncRoot;
        //public bool IsSynchronized => ((IList)base).IsSynchronized;
        //new object this[int index]
        //{
        //    get
        //    {
        //        using (_lock.Read())
        //            return base[index];
        //    }
        //    set
        //    {
        //        using (_lock.Write())
        //            base[index] = value;
        //    }
        //}
        public new T this[int index]
        {
            get
            {
                using (_lock.Read())
                    return base[index];
            }
            set
            {
                using (_lock.Write())
                    base[index] = value;
            }
        }

        public ThreadSafeList() : base()
        {
            //base = new List<T>();
        }
        public ThreadSafeList(int capacity) : base(capacity)
        {
            //base = new List<T>(capacity);
        }
        public ThreadSafeList(IEnumerable<T> list) : base(list)
        {
            //base = new List<T>(list);
        }
        public ThreadSafeList(ReaderWriterLockSlim threadLock) : base()
        {
            //base = new List<T>();
            _lock = threadLock;
        }

        public new void Add(T item)
        {
            using (_lock.Write())
                base.Add(item);
        }
        public new void AddRange(IEnumerable<T> collection)
        {
            using (_lock.Write())
                base.AddRange(collection);
        }
        public new bool Remove(T item)
        {
            using (_lock.Write())
                return base.Remove(item);
        }
        public new void RemoveRange(int index, int count)
        {
            using (_lock.Write())
                base.RemoveRange(index, count);
        }
        public new void RemoveAt(int index)
        {
            using (_lock.Write())
                base.RemoveAt(index);
        }
        public new void Clear()
        {
            using (_lock.Write())
                base.Clear();
        }
        public new void RemoveAll(Predicate<T> match)
        {
            using (_lock.Write())
                base.RemoveAll(match);
        }
        public new void Insert(int index, T item)
        {
            using (_lock.Write())
                base.Insert(index, item);
        }
        public new void InsertRange(int index, IEnumerable<T> collection)
        {
            using (_lock.Write())
                base.InsertRange(index, collection);
        }
        public new ReadOnlyCollection<T> AsReadOnly()
        {
            using (_lock.Read())
                return base.AsReadOnly();
        }
        public new int BinarySearch(int index, int count, T item, IComparer<T> comparer)
        {
            using (_lock.Read())
                return base.BinarySearch(index, count, item, comparer);
        }
        public new int BinarySearch(T item)
        {
            using (_lock.Read())
                return base.BinarySearch(item);
        }
        public new int BinarySearch(T item, IComparer<T> comparer)
        {
            using (_lock.Read())
                return base.BinarySearch(item, comparer);
        }
        public new bool Contains(T item)
        {
            using (_lock.Read())
                return base.Contains(item);
        }
        public new List<TOutput> ConvertAll<TOutput>(Converter<T, TOutput> converter)
        {
            using (_lock.Read())
                return base.ConvertAll(converter);
        }
        public new void CopyTo(T[] array, int arrayIndex)
        {
            using (_lock.Read())
                base.CopyTo(array, arrayIndex);
        }
        public new void CopyTo(int index, T[] array, int arrayIndex, int count)
        {
            using (_lock.Read())
                base.CopyTo(array, arrayIndex);
        }
        public new void CopyTo(T[] array)
        {
            using (_lock.Read())
                base.CopyTo(array);
        }
        public new bool Exists(Predicate<T> match)
        {
            using (_lock.Read())
                return base.Exists(match);
        }
        public new T? Find(Predicate<T> match)
        {
            using (_lock.Read())
                return base.Find(match);
        }
        public new List<T> FindAll(Predicate<T> match)
        {
            using (_lock.Read())
                return base.FindAll(match);
        }
        public new int FindIndex(Predicate<T> match)
        {
            using (_lock.Read())
                return base.FindIndex(match);
        }
        public new int FindIndex(int startIndex, Predicate<T> match)
        {
            using (_lock.Read())
                return base.FindIndex(startIndex, match);
        }
        public new int FindIndex(int startIndex, int count, Predicate<T> match)
        {
            using (_lock.Read())
                return base.FindIndex(startIndex, count, match);
        }
        public new T? FindLast(Predicate<T> match)
        {
            using (_lock.Read())
                return base.FindLast(match);
        }
        public new int FindLastIndex(Predicate<T> match)
        {
            using (_lock.Read())
                return base.FindLastIndex(match);
        }
        public new int FindLastIndex(int startIndex, Predicate<T> match)
        {
            using (_lock.Read())
                return base.FindLastIndex(startIndex, match);
        }
        public new int FindLastIndex(int startIndex, int count, Predicate<T> match)
        {
            using (_lock.Read())
                return base.FindLastIndex(startIndex, count, match);
        }
        public new void ForEach(Action<T> action)
        {
            using (_lock.Read())
                base.ForEach(action);
        }
        public new IEnumerator<T> GetEnumerator()
            => new ThreadSafeListEnumerator<T>(this, _lock);
        public new List<T> GetRange(int index, int count)
        {
            using (_lock.Read())
                return base.GetRange(index, count);
        }
        public new int IndexOf(T item, int index, int count)
        {
            using (_lock.Read())
                return base.IndexOf(item, index, count);
        }
        public new int IndexOf(T item, int index)
        {
            using (_lock.Read())
                return base.IndexOf(item, index);
        }
        public new int IndexOf(T item)
        {
            using (_lock.Read())
                return base.IndexOf(item);
        }
        public new int LastIndexOf(T item)
        {
            using (_lock.Read())
                return base.LastIndexOf(item);
        }
        public new int LastIndexOf(T item, int index)
        {
            using (_lock.Read())
                return base.LastIndexOf(item, index);
        }
        public new int LastIndexOf(T item, int index, int count)
        {
            using (_lock.Read())
                return base.LastIndexOf(item, index, count);
        }
        public new void Reverse(int index, int count)
        {
            using (_lock.Write())
                base.Reverse(index, count);
        }
        public new void Reverse()
        {
            using (_lock.Write())
                base.Reverse();
        }
        public new void Sort(int index, int count, IComparer<T> comparer)
        {
            using (_lock.Write())
                base.Sort(index, count, comparer);
        }
        public new void Sort(Comparison<T> comparison)
        {
            using (_lock.Write())
                base.Sort(comparison);
        }
        public new void Sort()
        {
            using (_lock.Write())
                base.Sort();
        }
        public new void Sort(IComparer<T> comparer)
        {
            using (_lock.Write())
                base.Sort(comparer);
        }
        public new T[] ToArray()
        {
            using (_lock.Read())
                return base.ToArray();
        }
        public new bool TrueForAll(Predicate<T> match)
        {
            using (_lock.Read())
                return base.TrueForAll(match);
        }

        //IEnumerator new IEnumerable.GetEnumerator()
        //    => GetEnumerator();
        
        //public int Add(object value)
        //{
        //    using (_lock.Write())
        //        return base.Add(value);
        //}
        //public bool Contains(object value)
        //{
        //    using (_lock.Read())
        //        return base.Contains(value);
        //}
        //public int IndexOf(object value)
        //{
        //    using (_lock.Read())
        //        return base.IndexOf(value);
        //}
        //public new void Insert(int index, object value)
        //{
        //    using (_lock.Write())
        //        ((IList)base).Insert(index, value);
        //}
        //public new void Remove(object value)
        //{
        //    using (_lock.Write())
        //        ((IList)base).Remove(value);
        //}
        //public new void CopyTo(Array array, int index)
        //{
        //    using (_lock.Read())
        //        ((IList)base).CopyTo(array, index);
        //}
    }
}

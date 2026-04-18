using XREngine.Extensions;
using System.Collections;

namespace System
{
    public class ThreadSafeHashSet<T> : ICollection<T>, IEnumerable<T>, IEnumerable, ISet<T>, IReadOnlyCollection<T>
    {
        protected readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        protected HashSet<T> _inner;

        public ThreadSafeHashSet()
        {
            _inner = new HashSet<T>();
        }
        public ThreadSafeHashSet(IEqualityComparer<T> comparer)
        {
            _inner = new HashSet<T>(comparer);
        }
        public ThreadSafeHashSet(IEnumerable<T> collection)
        {
            _inner = new HashSet<T>(collection);
        }
        public ThreadSafeHashSet(IEnumerable<T> collection, IEqualityComparer<T> comparer)
        {
            _inner = new HashSet<T>(collection, comparer);
        }

        public IEqualityComparer<T> Comparer
            => _inner.Comparer;

        public static IEqualityComparer<HashSet<T>> CreateSetComparer()
            => HashSet<T>.CreateSetComparer();

        public int Count
            => ((ICollection<T>)_inner).Count;

        public bool IsReadOnly
            => ((ICollection<T>)_inner).IsReadOnly;

        public void Add(T item)
        {
            using (_lock.Write())
                ((ICollection<T>)_inner).Add(item);
        }

        public void Clear()
        {
            using (_lock.Write())
                ((ICollection<T>)_inner).Clear();
        }

        public bool Contains(T item)
        {
            using (_lock.Read())
                return ((ICollection<T>)_inner).Contains(item);
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            using (_lock.Read())
                ((ICollection<T>)_inner).CopyTo(array, arrayIndex);
        }

        public void ExceptWith(IEnumerable<T> other)
        {
            using (_lock.Write())
                ((ISet<T>)_inner).ExceptWith(other);
        }

        public IEnumerator<T> GetEnumerator()
            => new ThreadSafeEnumerator<T>(_inner.GetEnumerator(), _lock);

        public void IntersectWith(IEnumerable<T> other)
        {
            using (_lock.Write())
                ((ISet<T>)_inner).IntersectWith(other);
        }

        public bool IsProperSubsetOf(IEnumerable<T> other)
        {
            using (_lock.Read())
                return ((ISet<T>)_inner).IsProperSubsetOf(other);
        }

        public bool IsProperSupersetOf(IEnumerable<T> other)
        {
            using (_lock.Read())
                return ((ISet<T>)_inner).IsProperSupersetOf(other);
        }

        public bool IsSubsetOf(IEnumerable<T> other)
        {
            using (_lock.Read())
                return ((ISet<T>)_inner).IsSubsetOf(other);
        }

        public bool IsSupersetOf(IEnumerable<T> other)
        {
            using (_lock.Read())
                return ((ISet<T>)_inner).IsSupersetOf(other);
        }
        
        public bool Overlaps(IEnumerable<T> other)
        {
            using (_lock.Read())
                return ((ISet<T>)_inner).Overlaps(other);
        }

        public bool Remove(T item)
        {
            using (_lock.Write())
                return ((ICollection<T>)_inner).Remove(item);
        }

        public bool SetEquals(IEnumerable<T> other)
        {
            using (_lock.Read())
                return ((ISet<T>)_inner).SetEquals(other);
        }

        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            using (_lock.Write())
                ((ISet<T>)_inner).SymmetricExceptWith(other);
        }

        public void UnionWith(IEnumerable<T> other)
        {
            using (_lock.Write())
                ((ISet<T>)_inner).UnionWith(other);
        }

        bool ISet<T>.Add(T item)
        {
            using (_lock.Write())
                return ((ISet<T>)_inner).Add(item);
        }

        IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();
                
        public void CopyTo(T[] array, int arrayIndex, int count)
        {
            using (_lock.Read())
                _inner.CopyTo(array, arrayIndex, count);
        }

        public void CopyTo(T[] array)
        {
            using (_lock.Read())
                _inner.CopyTo(array);
        }

        public int RemoveWhere(Predicate<T> match)
        {
            using (_lock.Write())
                return _inner.RemoveWhere(match);
        }

        public void TrimExcess()
            => _inner.TrimExcess();
    }
}

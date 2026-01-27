using System.Collections;
using Extensions;

namespace XREngine.Core
{
    /// <summary>
    /// A list that contains items whose indices never change even if items before them are removed.
    /// </summary>
    public class ConsistentIndexList<T> : IEnumerable<T>
    {
        private readonly List<T?> _list = [];
        private readonly List<int> _nullIndices = [];
        private readonly List<int> _activeIndices = [];
        private readonly ReaderWriterLockSlim _rwl = new();

        public int Count => _activeIndices.Count;
        public T this[int index]
        {
            get => _list[index]!;
            set
            {
                _rwl.EnterWriteLock();
                try
                {
                    var comp = EqualityComparer<T?>.Default;
                    if (comp.Equals(_list[index], default) && !comp.Equals(value, default))
                        _activeIndices.Add(index);
                    else if (!comp.Equals(_list[index], default) && comp.Equals(value, default))
                        _activeIndices.Remove(index);
                }
                finally
                {
                    _rwl.ExitWriteLock();
                }

                _list[index] = value;
            }
        }
        //public int IndexOfNextAddition(int offset)
        //{
        //    if (_nullIndices.Count > offset)
        //        return _nullIndices[offset];
        //    else
        //        return _list.Count + offset - _nullIndices.Count;
        //}
        public int Add(T item)
        {
            int index;
            if (_nullIndices.Count > 0)
            {
                index = _nullIndices[0];
                _nullIndices.RemoveAt(0);
                _list[index] = item;
            }
            else
            {
                index = _list.Count;
                _list.Add(item);
            }
            _rwl.EnterWriteLock();
            try
            {
                _activeIndices.Add(index);
            }
            finally
            {
                _rwl.ExitWriteLock();
            }
            return index;
        }
        public void Remove(T item)
        {
            RemoveAt(_list.IndexOf(item));
        }
        public void RemoveAt(int index)
        {
            if (!_list.IndexInRange(index))
                return;

            if (index == _list.Count - 1)
            {
                _list.RemoveAt(index);
                while (_list.Count > 0 && EqualityComparer<T?>.Default.Equals(_list[^1], default))
                    _list.RemoveAt(_list.Count - 1);
            }
            else
            {
                _list[index] = default;
                int addIndex = _nullIndices.BinarySearch(index);
                if (addIndex < 0)
                    addIndex = ~addIndex;
                _nullIndices.Insert(addIndex, index);
            }
            _rwl.EnterWriteLock();
            try
            {
                _activeIndices.Remove(index);
            }
            finally
            {
                _rwl.ExitWriteLock();
            }
        }

        public bool HasValueAtIndex(int index)
            => index >= 0 && index < _list.Count && !EqualityComparer<T?>.Default.Equals(_list[index], default);

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            _rwl.EnterReadLock();
            try
            {
                foreach (int i in _activeIndices)
                    if (_list.IndexInRange(i) && _list[i] is T item)
                        yield return item;
            }
            finally 
            {
                _rwl.ExitReadLock();
            }
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            _rwl.EnterReadLock();
            try
            {
                foreach (int i in _activeIndices)
                    if (_list.IndexInRange(i) && _list[i] is T item)
                        yield return item;
            }
            finally
            {
                _rwl.ExitReadLock();
            }
        }
    }
}


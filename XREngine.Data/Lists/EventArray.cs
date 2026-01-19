using Extensions;
using System.Collections.Specialized;

namespace System.Collections.Generic
{
    public class EventArray<T> : ICollection, ICloneable, IList, IEnumerable, IStructuralComparable, IStructuralEquatable, INotifyCollectionChanged
    {
        private T[] _array;

        public delegate void ItemSetHandler(T newItem, T oldItem, int index);
        public delegate void ItemMultiSetHandler(int[] indices);

        /// <summary>
        /// Event called after items in this list are changed.
        /// </summary>
        public event ItemMultiSetHandler? MultipleItemsChanged;
        /// <summary>
        /// Event called after an item in this list is changed.
        /// </summary>
        public event ItemSetHandler? ItemChanged;
        /// <summary>
        /// Event called before an item in this list is changed.
        /// </summary>
        public event Action? PreSet;
        /// <summary>
        /// Event called after an item in this list is changed.
        /// </summary>
        public event Action? PostSet;
        /// <summary>
        /// Event called before this list is modified in any way at all.
        /// </summary>
        public event Action? PreMultiSet;
        /// <summary>
        /// Event called after this list is modified in any way at all.
        /// </summary>
        public event Action? PostMultiSet;

        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        
        public EventArray(IEnumerable<T> list)
        {
            _array = list.ToArray();
        }
        public EventArray(int size)
        {
            _array = new T[size];
        }

        private HashSet<int> _changedIndices = new HashSet<int>();

        private bool _multiChange = false;

        public int Count => ((ICollection)_array).Count;

        public object SyncRoot => _array.SyncRoot;

        public bool IsSynchronized => _array.IsSynchronized;

        public bool IsReadOnly => false;

        public bool IsFixedSize => true;

        object? IList.this[int index]
        {
            get => _array[index];
            set => _array[index] = (T)(value ?? throw new ArgumentNullException(nameof(value)));
        }

        public void StartMultiChange()
        {
            _multiChange = true;
            PreMultiSet?.Invoke();
        }
        public void EndMultiChange()
        {
            _multiChange = false;
            PostMultiSet?.Invoke();
            MultipleItemsChanged?.Invoke(_changedIndices.ToArray());
            _changedIndices.Clear();
        }

        public void CopyTo(Array array, int index)
        {
            _array.CopyTo(array, index);
        }

        public IEnumerator GetEnumerator()
        {
            return _array.GetEnumerator();
        }

        public object Clone()
        {
            return new EventArray<T>(_array);
        }

        public int Add(object? value)
        {
            return ((IList)_array).Add(value);
        }

        public bool Contains(object? value)
        {
            return ((IList)_array).Contains(value);
        }

        public void Clear()
        {
            ((IList)_array).Clear();
        }

        public int IndexOf(object? value)
        {
            return ((IList)_array).IndexOf(value);
        }

        public void Insert(int index, object? value)
        {
            ((IList)_array).Insert(index, value);
        }

        public void Remove(object? value)
        {
            ((IList)_array).Remove(value);
        }

        public void RemoveAt(int index)
        {
            ((IList)_array).RemoveAt(index);
        }

        public int CompareTo(object? other, IComparer comparer)
        {
            return ((IStructuralComparable)_array).CompareTo(other, comparer);
        }

        public bool Equals(object? other, IEqualityComparer comparer)
        {
            return ((IStructuralEquatable)_array).Equals(other, comparer);
        }

        public int GetHashCode(IEqualityComparer comparer)
        {
            return ((IStructuralEquatable)_array).GetHashCode(comparer);
        }

        public T this[int index]
        {
            get => _array.IndexInRangeArrayT(index) ? _array[index] : default!;
            set
            {
                if (_array.IndexInRangeArrayT(index))
                {
                    T oldValue = _array[index];
                    PreSet?.Invoke();
                    _array[index] = value;
                    PostSet?.Invoke();
                    ItemChanged?.Invoke(value, oldValue, index);
                    if (!_multiChange)
                        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace));
                    else
                        _changedIndices.Add(index);
                }
            }
        }
    }
}

using XREngine.Extensions;
using System.Collections;
using System.ComponentModel;

namespace XREngine.Animation
{
    /// <summary>
    /// Represents a generic collection of keyframes to animate any property.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class KeyframeTrack<T> : BaseKeyframeTrack, IList, IList<T>, IEnumerable<T> where T : Keyframe, new()
    {
        private T? _first = null;
        private T? _last = null;

        [Browsable(false)]
        public T? First
        {
            get => _first;
            private set
            {
                _first = value;
                if (_first != null)
                    _first.OwningTrack = this;
            }
        }
        [Browsable(false)]
        public T? Last
        {
            get => _last;
            private set
            {
                _last = value;
                if (_last != null)
                    _last.OwningTrack = this;
            }
        }

        protected internal override Keyframe? FirstKey
        {
            get => First;
            internal set => First = value as T;
        }
        protected internal override Keyframe? LastKey
        {
            get => Last;
            internal set => Last = value as T;
        }

        [Browsable(false)]
        public bool IsReadOnly => false;

        [Browsable(false)]
        public bool IsFixedSize => false;

        [Browsable(false)]
        public object SyncRoot { get; } = new object();

        [Browsable(false)]
        public bool IsSynchronized { get; } = false;

        public T this[int index]
        {
            get
            {
                if (index >= 0 && index < Count)
                {
                    int i = 0;
                    foreach (var key in this)
                    {
                        if (i == index)
                            return (T)key;
                        ++i;
                    }
                }
                throw new IndexOutOfRangeException();
            }
            set
            {
                if (index < 0 || index > Count)
                    return;
                
                int i = 0;
                foreach (var key in this)
                {
                    if (i++ != index)
                        continue;
                    
                    key.Remove();
                    (key.Prev ?? key.Next)?.UpdateLink(value);
                    break;
                }
            }
        }

        object? IList.this[int index]
        {
            get
            {
                if (index >= 0 && index < Count)
                {
                    int i = 0;
                    foreach (var key in this)
                        if (i++ == index)
                            return key;
                }
                throw new IndexOutOfRangeException();
            }
            set
            {
                if (value is T keyValue && index >= 0 && index <= Count)
                {
                    int i = 0;
                    foreach (var key in this)
                    {
                        if (i++ != index)
                            continue;
                        
                        Keyframe? sibling = key.Prev ?? key.Next;
                        key.Remove();
                        sibling?.UpdateLink(keyValue);
                        break;
                    }
                }
            }
        }

        public void Add(IEnumerable<T> keys)
            => keys.ForEach(Add);
        public void Add(params T[] keys)
            => keys.ForEach(Add);

        public void Add(T key)
        {
            if (key is null)
                return;

            if (First is null)
            {
                //Reset key location before adding
                key.Remove();
                First = key;
                Last = key;
                Count = 1;
                OnChanged();
            }
            else
                First.UpdateLink(key);
        }
        public void RemoveLast()
        {
            if (Last is null)
                return;

            if (First == Last)
            {
                First = null;
                Last = null;
                --Count;
                OnChanged();
            }
            else
            {
                Keyframe temp = Last;
                Last = Last.Prev as T;
                temp.Remove();
            }
        }
        public void RemoveFirst()
        {
            if (First is null)
                return;

            if (First == Last)
            {
                First = null;
                Last = null;
                --Count;
                OnChanged();
            }
            else
            {
                Keyframe temp = First;
                First = First.Next as T;
                temp.Remove();
            }
        }
        public T? GetKeyBefore(float second)
        {
            // Walk the links directly. The track enumerator allocates, and sampling calls this each frame.
            T? bestKey = null;
            for (Keyframe? key = First; key is not null && key.Second <= second; key = key.Next)
                bestKey = key as T;

            return bestKey;
        }
        /// <summary>
        /// Finds the same key as <see cref="GetKeyBefore"/>, and starts the walk at a key in this track.
        /// </summary>
        /// <param name="second">The time to find a key for.</param>
        /// <param name="start">The key to start from, or null to start from the first key.</param>
        /// <param name="steps">The number of links that the walk followed.</param>
        internal T? FindKeyBefore(float second, T? start, out int steps)
        {
            steps = 0;
            Keyframe? key = start ?? First;
            while (key is not null && key.Second > second)
            {
                key = key.Prev;
                ++steps;
            }

            if (key is null)
                return null;

            for (Keyframe? next = key.Next; next is not null && next.Second <= second; next = key.Next)
            {
                key = next;
                ++steps;
            }

            return key as T;
        }

        public override IEnumerator<Keyframe> GetEnumerator()
        {
            Keyframe? node = First;
            do
            {
                if (node is null)
                    break;
                yield return node;
                node = node.Next;
            }
            while (node != null);
        }
        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            Keyframe? node = First;
            do
            {
                if (node is null)
                    break;
                yield return (T)node;
                node = node.Next;
            }
            while (node != null);
        }

        public int Add(object? value)
        {
            if (value is T key)
            {
                Add(key);
                return Count - 1;
            }
            return -1;
        }

        public bool Contains(object? value)
        {
            if (value is T keyValue)
                foreach (T key in this.Cast<T>())
                    if (key == keyValue)
                        return true;
            return false;
        }

        public void Clear()
        {
            _first = null;
            _last = null;
            Count = 0;
            ++Revision;
        }

        public int IndexOf(object? value)
        {
            if (value is not T keyValue)
                return -1;

            int i = 0;
            foreach (var key in this)
                if (key != keyValue)
                    ++i;
                else
                    return i;

            return -1;
        }

        public void Insert(int index, object? value)
        {
            throw new System.NotImplementedException();
        }

        public void Remove(object? value)
        {
            throw new System.NotImplementedException();
        }

        public void RemoveAt(int index)
        {
            throw new System.NotImplementedException();
        }

        public void CopyTo(Array array, int index)
        {
            throw new System.NotImplementedException();
        }

        public int IndexOf(T item)
        {
            throw new System.NotImplementedException();
        }

        public void Insert(int index, T item)
        {
            throw new System.NotImplementedException();
        }

        public bool Contains(T item)
        {
            throw new System.NotImplementedException();
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            throw new System.NotImplementedException();
        }

        public bool Remove(T item)
        {
            if (item.OwningTrack != this)
                return false;
            
            item.Remove();
            return true;
        }

        //TODO: write keyframe append method
        public void Append(KeyframeTrack<T> keyframes)
        {
            Keyframe? k = keyframes.First, temp;
            while (k != null)
            {
                temp = k.Next;
                k.Remove();
                k.Second += LengthInSeconds;
                Add(k);
                k = temp;
            }
        }
    }
}

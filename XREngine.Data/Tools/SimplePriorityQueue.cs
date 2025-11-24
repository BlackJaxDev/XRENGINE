using System.Collections.Generic;

namespace XREngine.Data.Tools
{
    public class SimplePriorityQueue<T>
    {
        private readonly List<KeyValuePair<T, float>> _heap = new();

        public SimplePriorityQueue()
        {
        }

        public void Enqueue(T item, float priority)
        {
            _heap.Add(new KeyValuePair<T, float>(item, priority));
            int index = _heap.Count - 1;
            while (index > 0)
            {
                int parentIndex = (index - 1) / 2;

                if (_heap[parentIndex].Value <= _heap[index].Value)
                    break;

                (_heap[parentIndex], _heap[index]) = (_heap[index], _heap[parentIndex]);
                index = parentIndex;
            }
        }

        public T Dequeue()
        {
            if (_heap.Count == 0)
            {
                throw new InvalidOperationException("The queue is empty.");
            }

            T result = _heap[0].Key;
            int lastIndex = _heap.Count - 1;
            _heap[0] = _heap[lastIndex];
            _heap.RemoveAt(lastIndex);

            int index = 0;
            while (true)
            {
                int leftChildIndex = 2 * index + 1;
                int rightChildIndex = 2 * index + 2;
                int minChildIndex;

                if (leftChildIndex >= _heap.Count)
                    break;

                if (rightChildIndex >= _heap.Count)
                    minChildIndex = leftChildIndex;
                else
                    minChildIndex = _heap[leftChildIndex].Value < _heap[rightChildIndex].Value
                        ? leftChildIndex
                        : rightChildIndex;

                if (_heap[index].Value <= _heap[minChildIndex].Value)
                    break;

                (_heap[minChildIndex], _heap[index]) = (_heap[index], _heap[minChildIndex]);
                index = minChildIndex;
            }

            return result;
        }

        private void UpHeap(int index)
        {
            while (index > 0)
            {
                int parentIndex = (index - 1) / 2;
                if (_heap[parentIndex].Value <= _heap[index].Value) break;

                (_heap[parentIndex], _heap[index]) = (_heap[index], _heap[parentIndex]);
                index = parentIndex;
            }
        }

        private void DownHeap(int index)
        {
            while (true)
            {
                int leftChildIndex = 2 * index + 1;
                int rightChildIndex = 2 * index + 2;
                int minChildIndex;

                if (leftChildIndex >= _heap.Count) break;
                if (rightChildIndex >= _heap.Count) minChildIndex = leftChildIndex;
                else minChildIndex = _heap[leftChildIndex].Value < _heap[rightChildIndex].Value ? leftChildIndex : rightChildIndex;

                if (_heap[index].Value <= _heap[minChildIndex].Value) break;

                KeyValuePair<T, float> temp = _heap[index];
                _heap[index] = _heap[minChildIndex];
                _heap[minChildIndex] = temp;
                index = minChildIndex;
            }
        }

        public void Remove(T item)
        {
            int index = _heap.FindIndex(pair => EqualityComparer<T>.Default.Equals(pair.Key, item));
            if (index == -1) return;

            int lastIndex = _heap.Count - 1;
            _heap[index] = _heap[lastIndex];
            _heap.RemoveAt(lastIndex);

            if (index < lastIndex)
            {
                UpHeap(index);
                DownHeap(index);
            }
        }

        public void UpdatePriority(T item, float newPriority)
        {
            int index = _heap.FindIndex(pair => EqualityComparer<T>.Default.Equals(pair.Key, item));
            if (index == -1) return;

            float oldPriority = _heap[index].Value;
            _heap[index] = new KeyValuePair<T, float>(item, newPriority);

            if (newPriority < oldPriority)
            {
                UpHeap(index);
            }
            else
            {
                DownHeap(index);
            }
        }

        public int Count()
        {
            return _heap.Count;
        }
    }
}

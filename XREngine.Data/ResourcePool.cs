using System.Collections.Concurrent;

namespace XREngine.Core
{
    public interface IPoolable
    {
        /// <summary>
        /// Called when an object is taken from the pool.
        /// </summary>
        void OnPoolableReset();
        /// <summary>
        /// Called when an object is released back into the pool.
        /// </summary>
        void OnPoolableReleased();
        /// <summary>
        /// Called when the pool is at capacity so the item must be fully destroyed.
        /// </summary>
        void OnPoolableDestroyed();
    }
    public class ResourcePool<T> where T : IPoolable
    {
        private readonly ConcurrentBag<T> _objects = [];
        private readonly Func<T> _generator;
        private int _capacity = int.MaxValue;

        public int Capacity
        {
            get => _capacity;
            set
            {
                _capacity = value;
                if (_objects.Count > _capacity)
                    Destroy(_objects.Count - _capacity);
            }
        }

        public ResourcePool(Func<T> generator, int capacity = int.MaxValue)
            : this(0, generator, capacity) { }
        public ResourcePool(int initialCount, Func<T> generator, int capacity = int.MaxValue)
        {
            _capacity = capacity;
            _generator = generator ?? throw new ArgumentNullException(nameof(generator));
            int loopCount = Math.Min(initialCount, capacity);
            for (int i = 0; i < loopCount; ++i)
                _objects.Add(_generator());
        }
        public T Take()
        {
            if (!_objects.TryTake(out T? item))
                item = _generator();
            
            item.OnPoolableReset();
            return item;
        }
        public void Release(T item)
        {
            item.OnPoolableReleased();

            if (_objects.Count < _capacity)
                _objects.Add(item);
            else
                item.OnPoolableDestroyed();
        }
        public void Destroy(int count)
        {
            for (int i = 0; i < count && !_objects.IsEmpty; ++i)
                if (_objects.TryTake(out T? item))
                    item.OnPoolableDestroyed();
        }
    }
}

using System.Collections.Concurrent;

namespace XREngine.Core
{
    /// <summary>
    /// IPoolable is an interface that can be implemented by any class that wants to be used with a ResourcePool.
    /// It defines three methods that are called by the ResourcePool when an object is 
    /// taken from the pool, 
    /// released back to the pool, 
    /// or destroyed when the pool is at capacity.
    /// </summary>
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

    /// <summary>
    /// ResourcePool is a thread-safe pool of objects that can be reused to avoid the overhead of creating and destroying objects.
    /// </summary>
    /// <typeparam name="T">The type of objects to be pooled. Must implement the IPoolable interface.</typeparam>
    public class ResourcePool<T> where T : IPoolable
    {
        /// <summary>
        /// The collection of objects in the pool. 
         /// This is a ConcurrentBag, which is a thread-safe collection that allows for fast adding and removing of items.
         /// The ConcurrentBag is used to store the objects in the pool, and it allows for multiple threads to take and release objects from the pool without the need for locking.
        /// </summary>
        private readonly ConcurrentBag<T> _objects = [];
        /// <summary>
        /// The function used to generate new instances when the pool is empty.
        /// </summary>
        private readonly Func<T> _generator;
        private int _capacity = int.MaxValue;

        /// <summary>
        /// The maximum number of objects that can be stored in the pool.
        /// </summary>
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

        /// <summary>
        /// Creates a new ResourcePool with the specified generator function and capacity. 
         /// The generator function is used to create new instances of the pooled objects when the pool is empty.
        /// </summary>
        /// <param name="generator">The function used to generate new instances of the pooled objects.</param>
        /// <param name="capacity">The maximum number of objects that can be stored in the pool.</param>
        public ResourcePool(Func<T> generator, int capacity = int.MaxValue)
            : this(0, generator, capacity) { }

        /// <summary>
        /// Creates a new ResourcePool with the specified initial count, generator function, and capacity. 
         /// The generator function is used to create new instances of the pooled objects when the pool is empty.
         /// The initial count specifies how many objects should be pre-created and added to the pool when it is created.
         /// If the initial count is greater than the capacity, only capacity number of objects will be created and added to the pool.
         /// This constructor can be used to create a pool that is pre-populated with a certain number of objects, which can help reduce the overhead of creating objects when they are first needed.
         /// However, it is important to ensure that the initial count does not exceed the capacity, as this can lead to unexpected behavior when taking and releasing objects from the pool.
         /// If the initial count is less than or equal to the capacity, then all of the initial objects will be created and added to the pool as expected.
        /// </summary>
        /// <param name="initialCount">The number of objects to pre-create and add to the pool.</param>
        /// <param name="generator">The function used to generate new instances of the pooled objects.</param>
        /// <param name="capacity">The maximum number of objects that can be stored in the pool.</param>
        /// <exception cref="ArgumentNullException"></exception>
        public ResourcePool(int initialCount, Func<T> generator, int capacity = int.MaxValue)
        {
            _capacity = capacity;
            _generator = generator ?? throw new ArgumentNullException(nameof(generator));
            int loopCount = Math.Min(initialCount, capacity);
            for (int i = 0; i < loopCount; ++i)
                _objects.Add(_generator());
        }

        /// <summary>
        /// Takes an object from the pool. 
        /// If the pool is empty, a new object is created using the generator function.
        /// The OnPoolableReset method is called on the object before it is returned, 
        /// allowing it to be reset to a default state.
        /// </summary>
        /// <returns>The object taken from the pool.</returns>
        public T Take()
        {
            if (!_objects.TryTake(out T? item))
                item = _generator();
            
            item.OnPoolableReset();
            return item;
        }

        /// <summary>
        /// Releases an object back to the pool. 
        /// If the pool is full, the object's OnPoolableDestroyed method is called.
        /// </summary>
        /// <param name="item">The object to release back to the pool.</param>
        public void Release(T item)
        {
            item.OnPoolableReleased();

            if (_objects.Count < _capacity)
                _objects.Add(item);
            else
                item.OnPoolableDestroyed();
        }

        /// <summary>
        /// Destroys a specified number of objects from the pool.
        /// </summary>
        /// <param name="count">The number of objects to destroy.</param>
        public void Destroy(int count)
        {
            for (int i = 0; i < count && !_objects.IsEmpty; ++i)
                if (_objects.TryTake(out T? item))
                    item.OnPoolableDestroyed();
        }
    }
}

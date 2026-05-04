using XREngine.Core;

namespace XREngine
{
    /// <summary>
    /// StateObject is a utility class that can be used to track the state of an operation or a series of operations.
    /// It can be used to track the state of an animation, a particle system, or any other operation that has a beginning and an end.
    /// The StateObject can be used to trigger events when the state changes, and it can also be used to store arbitrary state data that can be accessed when the state ends.
    /// </summary>
    public class StateObject : IDisposable, IPoolable
    {
        /// <summary>
        /// A pool of StateObject instances that can be reused to avoid unnecessary allocations.
        /// </summary>
        private static readonly ResourcePool<StateObject> _statePool = new(() => new());
        
        /// <summary>
        /// Indicates whether the StateObject has been disposed.
        /// This is used to prevent multiple disposals of the same StateObject, 
        /// which can lead to unexpected behavior and bugs.
        /// </summary>
        private bool _disposed = false;
        /// <summary>
        /// Indicates whether the StateObject was taken from the pool.
        /// This is used to determine whether the StateObject should be returned to the pool when it is disposed.
        /// </summary>
        private bool _takenFromStatePool = false;
        /// <summary>
        /// An optional state object that can be passed to the OnStateEnded action when the state ends.
        /// </summary>
        private object? _state;
        /// <summary>
        /// An optional action that is called when the state ends, 
        /// and is passed the state object as a parameter.
        /// </summary>
        private Action<object?>? _onStateEndedWithState;

        /// <summary>
        /// Initializes a new instance of the StateObject class.
        /// This constructor is private to prevent direct instantiation of the StateObject class,
        /// and to ensure that StateObjects are only created through the New method, 
        /// which takes care of managing the pool and the state of the StateObject.
        /// </summary>
        internal StateObject()
        {
            _takenFromStatePool = false;
        }

        /// <summary>
        /// An action that is called when the state ends.
        /// </summary>
        public Action? OnStateEnded { get; set; }
        
        /// <summary>
        /// Creates a new StateObject. 
        /// The StateObject is taken from the pool, 
        /// and will be returned to the pool when it is disposed.
        /// The OnStateEnded action will be called when the state ends, 
        /// and the StateObject is returned to the pool. 
        /// The OnStateEnded action can be used to trigger events or perform cleanup when the state ends.
        /// </summary>
        /// <param name="onStateEnded">The action to be called when the state ends.</param>
        /// <returns>A new StateObject instance.</returns>
        public static StateObject New(Action? onStateEnded = null)
        {
            var state = _statePool.Take();
            state._takenFromStatePool = true;
            state.OnStateEnded = onStateEnded;
            return state;
        }

        /// <summary>
        /// Creates a new StateObject with state.
        /// The StateObject is taken from the pool, 
        /// and will be returned to the pool when it is disposed.
        /// The OnStateEnded action will be called when the state ends,
        /// and the StateObject is returned to the pool. 
        /// The OnStateEnded action can be used to trigger events or perform cleanup when the state ends.
        /// </summary>
        /// <param name="onStateEnded">The action to be called when the state ends.</param>
        /// <param name="stateObject">The state object to be passed to the OnStateEnded action.</param>
        /// <returns>A new StateObject instance.</returns>
        public static StateObject New(Action<object?> onStateEnded, object? stateObject)
        {
            var state = _statePool.Take();
            state._takenFromStatePool = true;
            state._onStateEndedWithState = onStateEnded;
            state._state = stateObject;
            return state;
        }

        /// <summary>
        /// Called when the StateObject is destroyed.
        /// </summary>
        public void OnPoolableDestroyed()
        {
            OnStateEnded = null;
            _onStateEndedWithState = null;
            _state = null;
        }

        /// <summary>
        /// Called when the StateObject is released back to the pool.
        /// </summary>
        public void OnPoolableReleased()
        {
            if (_onStateEndedWithState is not null)
            {
                _onStateEndedWithState(_state);
                _onStateEndedWithState = null;
                _state = null;
            }
            else
            {
                OnStateEnded?.Invoke();
                OnStateEnded = null;
            }
        }

        /// <summary>
        /// Called when the StateObject is reset.
        /// This can be used to reset any state data that is stored in the StateObject,
        /// and to clear any references to other objects that may be stored in the StateObject.
        /// </summary>
        public void OnPoolableReset()
        {
            OnStateEnded = null;
            _onStateEndedWithState = null;
            _state = null;
            _disposed = false;
        }

        /// <summary>
        /// Disposes the StateObject, returning it to the pool if it was taken from the pool.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            GC.SuppressFinalize(this);
            if (_takenFromStatePool)
                _statePool.Release(this);
        }
    }
}
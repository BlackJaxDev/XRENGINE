using XREngine.Core;

namespace XREngine
{
    public class StateObject : IDisposable, IPoolable
    {
        private static readonly ResourcePool<StateObject> _statePool = new(() => new());
        private bool _takenFromStatePool = false;

        private Action<object?>? _onStateEndedWithState;
        private object? _state;

        internal StateObject()
        {
            _takenFromStatePool = false;
        }

        public Action? OnStateEnded { get; set; }
        private bool _disposed = false;

        public static StateObject New(Action? onStateEnded = null)
        {
            var state = _statePool.Take();
            state._takenFromStatePool = true;
            state.OnStateEnded = onStateEnded;
            return state;
        }

        public static StateObject New(Action<object?> onStateEnded, object? stateObject)
        {
            var state = _statePool.Take();
            state._takenFromStatePool = true;
            state._onStateEndedWithState = onStateEnded;
            state._state = stateObject;
            return state;
        }

        public void OnPoolableDestroyed()
        {
            OnStateEnded = null;
            _onStateEndedWithState = null;
            _state = null;
        }
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
        public void OnPoolableReset()
        {
            OnStateEnded = null;
            _onStateEndedWithState = null;
            _state = null;
            _disposed = false;
        }

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
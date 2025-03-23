using XREngine.Core;

namespace XREngine
{
    public class StateObject : IDisposable, IPoolable
    {
        private static readonly ResourcePool<StateObject> _statePool = new(() => new());
        private bool _takenFromStatePool = false;

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

        public void OnPoolableDestroyed() => OnStateEnded = null;
        public void OnPoolableReleased()
        {
            OnStateEnded?.Invoke();
            OnStateEnded = null;
        }
        public void OnPoolableReset()
        {
            OnStateEnded = null;
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
using XREngine.Core;

namespace XREngine
{
    public class StateObject : IDisposable, IPoolable
    {
        private static readonly ResourcePool<StateObject> _statePool = new(() => new());

        public Action? OnStateEnded { get; set; }
        private bool _disposed = false;

        public static StateObject New(Action? onStateEnded = null)
        {
            var state = _statePool.Take();
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
            _statePool.Release(this);
        }
    }
}
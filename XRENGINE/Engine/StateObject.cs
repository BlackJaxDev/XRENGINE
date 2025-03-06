using XREngine.Core;

namespace XREngine
{
    public class StateObject : IDisposable, IPoolable
    {
        private static readonly ResourcePool<StateObject> _statePool = new(() => new());

        public Action? OnStateEnded { get; set; }

        public static StateObject New(Action? onStateEnded = null)
        {
            var state = _statePool.Take();
            state.OnStateEnded = onStateEnded;
            return state;
        }

        public void OnPoolableDestroyed() => OnStateEnded = null;
        public void OnPoolableReleased() => OnStateEnded = null;
        public void OnPoolableReset() => OnStateEnded = null;

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            OnStateEnded?.Invoke();
            _statePool.Release(this);
        }
    }
}
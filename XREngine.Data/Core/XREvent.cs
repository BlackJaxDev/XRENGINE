using System.Collections;
using System.Collections.Concurrent;
using System.Threading;

namespace XREngine.Data.Core
{
    /// <summary>
    /// Event system for XR.
    /// </summary>
    public class XREvent : IEnumerable<Action>
    {
        public XREvent() { }

        private List<Action>? _actions;
        private List<Action> Actions => _actions ??= [];

        private ConcurrentQueue<Action>? _pendingAdds;
        private ConcurrentQueue<Action> PendingAdds => _pendingAdds ??= [];

        private ConcurrentQueue<Action>? _pendingRemoves;
        private ConcurrentQueue<Action> PendingRemoves => _pendingRemoves ??= [];

        public int Count => Actions.Count;

        public void AddListener(Action action)
        {
            PendingAdds.Enqueue(action);
        }

        public void RemoveListener(Action action)
        {
            PendingRemoves.Enqueue(action);
        }

        public void Invoke()
        {
            ConsumeQueues();
            Actions.ForEach(x => x.Invoke());
        }

        public async Task InvokeAsync()
        {
            ConsumeQueues();
            await Task.WhenAll([.. Actions.Select(Task.Run)]);
        }

        private void ConsumeQueues()
        {
            while (PendingAdds.TryDequeue(out Action? add))
                Actions.Add(add);
            while (PendingRemoves.TryDequeue(out Action? remove))
                Actions.Remove(remove);
        }

        public IEnumerator<Action> GetEnumerator()
            => ((IEnumerable<Action>)Actions).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()
            => ((IEnumerable)Actions).GetEnumerator();

        public static XREvent? operator +(XREvent? e, Action a)
        {
            e ??= new();
            e.AddListener(a);
            return e;
        }
        public static XREvent? operator -(XREvent? e, Action a)
        {
            if (e is null)
                return null;
            e.RemoveListener(a);
            if (e.Count == 0)
                return null;
            return e;
        }
    }
    /// <summary>
    /// Event system for XR, with a single argument.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class XREvent<T> : IEnumerable<Action<T>>
    {
        public XREvent() { }

        private List<Action<T>>? _actions;
        private List<Action<T>> Actions => _actions ??= [];

        private ConcurrentQueue<Action<T>>? _pendingAdds;
        private ConcurrentQueue<Action<T>> PendingAdds => _pendingAdds ??= [];

        private ConcurrentQueue<Action<T>>? _pendingRemoves;
        private ConcurrentQueue<Action<T>> PendingRemoves => _pendingRemoves ??= [];

        public int Count => Actions.Count;

        public void AddListener(Action<T> action)
        {
            PendingAdds.Enqueue(action);
        }

        public void RemoveListener(Action<T> action)
        {
            PendingRemoves.Enqueue(action);
        }

        public void Invoke(T item)
        {
            ConsumeQueues();
            Actions.ForEach(x => x.Invoke(item));
        }

        public async Task InvokeAsync(T item)
        {
            ConsumeQueues();
            await Task.WhenAll([.. Actions.Select(x => new Task(() => x.Invoke(item)))]);
        }

        private void ConsumeQueues()
        {
            while (PendingAdds.TryDequeue(out Action<T>? add))
                Actions.Add(add);
            while (PendingRemoves.TryDequeue(out Action<T>? remove))
                Actions.Remove(remove);
        }

        public IEnumerator<Action<T>> GetEnumerator()
            => ((IEnumerable<Action<T>>)Actions).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => ((IEnumerable)Actions).GetEnumerator();

        public static XREvent<T>? operator +(XREvent<T>? e, Action<T> a)
        {
            e ??= new();
            e.AddListener(a);
            return e;
        }
        public static XREvent<T>? operator -(XREvent<T>? e, Action<T> a)
        {
            if (e is null)
                return null;
            e.RemoveListener(a);
            if (e.Count == 0)
                return null;
            return e;
        }
    }
}

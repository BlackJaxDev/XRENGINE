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
        public static Func<string, IDisposable>? ProfilingHook = null;

        public XREvent() { }

        private List<Action>? _actions;
        private List<Action> Actions => _actions ??= [];

        private ConcurrentQueue<Action>? _pendingAdds;
        private ConcurrentQueue<Action> PendingAdds => _pendingAdds ??= [];

        private int _pendingAddsCount;

        private ConcurrentQueue<Action>? _pendingRemoves;
        private ConcurrentQueue<Action> PendingRemoves => _pendingRemoves ??= [];

        private int _pendingRemovesCount;

        private List<Action>? _removeBuffer;
        private List<Action> RemoveBuffer => _removeBuffer ??= [];

        private Dictionary<Action, int>? _removeCounts;
        private Dictionary<Action, int> RemoveCounts => _removeCounts ??= [];

        public int Count => Actions.Count;

        public bool HasPendingAdds => Volatile.Read(ref _pendingAddsCount) != 0;
        public bool HasPendingRemoves => Volatile.Read(ref _pendingRemovesCount) != 0;

        public void AddListener(Action action)
        {
            PendingAdds.Enqueue(action);
            Interlocked.Increment(ref _pendingAddsCount);
        }

        public void RemoveListener(Action action)
        {
            PendingRemoves.Enqueue(action);
            Interlocked.Increment(ref _pendingRemovesCount);
        }

        public void Invoke()
        {
            if (ProfilingHook is not null)
            {
                using var sample = ProfilingHook("XREvent.Invoke");
                InvokeInternal();
            }
            else
                InvokeInternal();
        }

        private void InvokeInternal()
        {
            ConsumeQueues();
            Actions.ForEach(x => x.Invoke());
        }

        public async Task InvokeAsync()
        {
            if (ProfilingHook is not null)
            {
                using var sample = ProfilingHook("XREvent.InvokeAsync");
                await InvokeAsyncInternal();
            }
            else
                await InvokeAsyncInternal();
        }

        private async Task InvokeAsyncInternal()
        {
            ConsumeQueues();
            await Task.WhenAll([.. Actions.Select(Task.Run)]);
        }

        public void InvokeParallel()
        {
            if (ProfilingHook is not null)
            {
                using var sample = ProfilingHook("XREvent.InvokeParallel");
                InvokeParallelInternal();
            }
            else
                InvokeParallelInternal();
        }

        private void InvokeParallelInternal()
        {
            ConsumeQueues();
            Parallel.ForEach(Actions, x => x.Invoke());
        }

        private void ConsumeQueues()
        {
            if (!HasPendingAdds && !HasPendingRemoves)
                return;

            if (ProfilingHook is not null)
            {
                using var sample = ProfilingHook("XREvent.ConsumeQueues");
                ConsumeQueuesInternal();
            }
            else
                ConsumeQueuesInternal();
        }

        private void ConsumeQueuesInternal()
        {
            if (!HasPendingAdds && !HasPendingRemoves)
                return;

            while (PendingAdds.TryDequeue(out Action? add))
            {
                Interlocked.Decrement(ref _pendingAddsCount);
                Actions.Add(add);
            }

            if (!HasPendingRemoves)
                return;

            var removes = RemoveBuffer;
            removes.Clear();
            while (PendingRemoves.TryDequeue(out Action? remove))
            {
                Interlocked.Decrement(ref _pendingRemovesCount);
                removes.Add(remove);
            }

            ApplyRemovals(Actions, removes);
        }

        private void ApplyRemovals(List<Action> actions, List<Action> removes)
        {
            if (removes.Count == 0 || actions.Count == 0)
                return;
            if (removes.Count == 1)
            {
                actions.Remove(removes[0]);
                return;
            }

            var counts = RemoveCounts;
            counts.Clear();
            for (int i = 0; i < removes.Count; i++)
            {
                var r = removes[i];
                if (counts.TryGetValue(r, out int c))
                    counts[r] = c + 1;
                else
                    counts.Add(r, 1);
            }

            int write = 0;
            for (int read = 0; read < actions.Count; read++)
            {
                var a = actions[read];
                if (counts.TryGetValue(a, out int remaining) && remaining > 0)
                {
                    if (remaining == 1)
                        counts.Remove(a);
                    else
                        counts[a] = remaining - 1;
                    continue;
                }

                actions[write++] = a;
            }

            if (write != actions.Count)
                actions.RemoveRange(write, actions.Count - write);
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
            if (e.Count == 0 && !e.HasPendingAdds && !e.HasPendingRemoves)
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

        private int _pendingAddsCount;

        private ConcurrentQueue<Action<T>>? _pendingRemoves;
        private ConcurrentQueue<Action<T>> PendingRemoves => _pendingRemoves ??= [];

        private int _pendingRemovesCount;

        private List<Action<T>>? _removeBuffer;
        private List<Action<T>> RemoveBuffer => _removeBuffer ??= [];

        private Dictionary<Action<T>, int>? _removeCounts;
        private Dictionary<Action<T>, int> RemoveCounts => _removeCounts ??= [];

        public int Count => Actions.Count;

        public bool HasPendingAdds => Volatile.Read(ref _pendingAddsCount) != 0;
        public bool HasPendingRemoves => Volatile.Read(ref _pendingRemovesCount) != 0;

        public void AddListener(Action<T> action)
        {
            PendingAdds.Enqueue(action);
            Interlocked.Increment(ref _pendingAddsCount);
        }

        public void RemoveListener(Action<T> action)
        {
            PendingRemoves.Enqueue(action);
            Interlocked.Increment(ref _pendingRemovesCount);
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
            if (!HasPendingAdds && !HasPendingRemoves)
                return;

            while (PendingAdds.TryDequeue(out Action<T>? add))
            {
                Interlocked.Decrement(ref _pendingAddsCount);
                Actions.Add(add);
            }

            if (!HasPendingRemoves)
                return;

            var removes = RemoveBuffer;
            removes.Clear();
            while (PendingRemoves.TryDequeue(out Action<T>? remove))
            {
                Interlocked.Decrement(ref _pendingRemovesCount);
                removes.Add(remove);
            }

            ApplyRemovals(Actions, removes);
        }

        private void ApplyRemovals(List<Action<T>> actions, List<Action<T>> removes)
        {
            if (removes.Count == 0 || actions.Count == 0)
                return;
            if (removes.Count == 1)
            {
                actions.Remove(removes[0]);
                return;
            }

            var counts = RemoveCounts;
            counts.Clear();
            for (int i = 0; i < removes.Count; i++)
            {
                var r = removes[i];
                if (counts.TryGetValue(r, out int c))
                    counts[r] = c + 1;
                else
                    counts.Add(r, 1);
            }

            int write = 0;
            for (int read = 0; read < actions.Count; read++)
            {
                var a = actions[read];
                if (counts.TryGetValue(a, out int remaining) && remaining > 0)
                {
                    if (remaining == 1)
                        counts.Remove(a);
                    else
                        counts[a] = remaining - 1;
                    continue;
                }

                actions[write++] = a;
            }

            if (write != actions.Count)
                actions.RemoveRange(write, actions.Count - write);
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
            if (e.Count == 0 && !e.HasPendingAdds && !e.HasPendingRemoves)
                return null;
            return e;
        }
    }
}

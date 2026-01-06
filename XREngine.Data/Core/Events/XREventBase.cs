using System.Collections;
using System.Collections.Concurrent;

namespace XREngine.Data.Core
{
    public abstract class XREventBase<TListener> : IEnumerable<TListener>
        where TListener : Delegate
    {
        protected XREventBase() { }

        protected virtual IDisposable? BeginProfiling(string name)
            => null;

        protected void WithProfiling(string name, Action action)
        {
            var sample = BeginProfiling(name);
            if (sample is null)
            {
                action();
                return;
            }

            using (sample)
                action();
        }

        protected async Task WithProfilingAsync(string name, Func<Task> action)
        {
            var sample = BeginProfiling(name);
            if (sample is null)
            {
                await action();
                return;
            }

            using (sample)
                await action();
        }

        private List<TListener>? _actions;
        protected List<TListener> Actions => _actions ??= [];

        private ConcurrentQueue<TListener>? _pendingAdds;
        private ConcurrentQueue<TListener> PendingAdds => _pendingAdds ??= [];

        private int _pendingAddsCount;

        private ConcurrentQueue<TListener>? _pendingRemoves;
        private ConcurrentQueue<TListener> PendingRemoves => _pendingRemoves ??= [];

        private int _pendingRemovesCount;

        private List<TListener>? _removeBuffer;
        private List<TListener> RemoveBuffer => _removeBuffer ??= [];

        private Dictionary<TListener, int>? _removeCounts;
        private Dictionary<TListener, int> RemoveCounts => _removeCounts ??= [];

        public int Count => Actions.Count;

        public bool HasPendingAdds => Volatile.Read(ref _pendingAddsCount) != 0;
        public bool HasPendingRemoves => Volatile.Read(ref _pendingRemovesCount) != 0;

        public void AddListener(TListener action)
        {
            PendingAdds.Enqueue(action);
            Interlocked.Increment(ref _pendingAddsCount);
        }

        public void RemoveListener(TListener action)
        {
            PendingRemoves.Enqueue(action);
            Interlocked.Increment(ref _pendingRemovesCount);
        }

        protected void ConsumeQueues(string profilingName)
        {
            if (!HasPendingAdds && !HasPendingRemoves)
                return;

            WithProfiling(profilingName, ConsumeQueuesInternal);
        }

        private void ConsumeQueuesInternal()
        {
            if (!HasPendingAdds && !HasPendingRemoves)
                return;

            while (PendingAdds.TryDequeue(out TListener? add))
            {
                Interlocked.Decrement(ref _pendingAddsCount);
                Actions.Add(add);
            }

            if (!HasPendingRemoves)
                return;

            var removes = RemoveBuffer;
            removes.Clear();
            while (PendingRemoves.TryDequeue(out TListener? remove))
            {
                Interlocked.Decrement(ref _pendingRemovesCount);
                removes.Add(remove);
            }

            ApplyRemovals(Actions, removes);
        }

        private void ApplyRemovals(List<TListener> actions, List<TListener> removes)
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

        public IEnumerator<TListener> GetEnumerator()
            => ((IEnumerable<TListener>)Actions).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator()
            => ((IEnumerable)Actions).GetEnumerator();
    }
}

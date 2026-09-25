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

        protected virtual bool HasProfilingHooks => false;

        protected virtual object? CaptureLinkedProfilingContext()
            => null;

        protected virtual IDisposable? BeginLinkedProfiling(object? context, string name)
            => BeginProfiling(name);

        protected IDisposable? BeginListenerProfiling(string prefix, TListener listener, int index)
        {
            if (!HasProfilingHooks)
                return null;

            return BeginProfiling(GetListenerProfilingName(prefix, listener, index));
        }

        protected IDisposable? BeginLinkedListenerProfiling(object? context, string prefix, TListener listener, int index)
        {
            if (!HasProfilingHooks)
                return null;

            string name = GetListenerProfilingName(prefix, listener, index);
            return context is null
                ? BeginProfiling(name)
                : BeginLinkedProfiling(context, name);
        }

        private Dictionary<(string Prefix, TListener Listener, int Index), string>? _listenerProfilingNames;

        private string GetListenerProfilingName(string prefix, TListener listener, int index)
        {
            Dictionary<(string Prefix, TListener Listener, int Index), string> cache =
                _listenerProfilingNames ??= [];
            var key = (prefix, listener, index);
            if (cache.TryGetValue(key, out string? cached))
                return cached;

            var method = listener.Method;
            string owner = method.DeclaringType?.FullName
                ?? listener.Target?.GetType().FullName
                ?? "<unknown>";
            string name = $"{prefix}[{index}] {owner}.{method.Name}";
            cache.Add(key, name);
            return name;
        }

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

        // Additions are applied on the next invocation. A removal that arrives
        // while its addition is still pending cancels it instead of queueing a
        // second entry: an event that is rarely or never invoked would otherwise
        // retain every add/remove pair (and the listener targets) indefinitely.
        private readonly Lock _pendingAddsLock = new();
        private List<TListener>? _pendingAdds;
        private List<TListener>? _pendingAddsSpare;

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
            using (_pendingAddsLock.EnterScope())
            {
                (_pendingAdds ??= []).Add(action);
                Interlocked.Increment(ref _pendingAddsCount);
            }
        }

        public void RemoveListener(TListener action)
        {
            using (_pendingAddsLock.EnterScope())
            {
                // Net listener counts match applying the pending add and then this
                // removal; the unapplied add simply never reaches the action list.
                int pendingIndex = _pendingAdds?.LastIndexOf(action) ?? -1;
                if (pendingIndex >= 0)
                {
                    _pendingAdds!.RemoveAt(pendingIndex);
                    Interlocked.Decrement(ref _pendingAddsCount);
                    return;
                }
            }

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

            _listenerProfilingNames?.Clear();
            if (HasPendingAdds)
            {
                // Swap the pending list for the empty spare under the lock so
                // listener mutation on other threads never waits on the action list.
                List<TListener>? adds;
                using (_pendingAddsLock.EnterScope())
                {
                    adds = _pendingAdds is { Count: > 0 } pending ? pending : null;
                    if (adds is not null)
                    {
                        _pendingAdds = _pendingAddsSpare;
                        _pendingAddsSpare = null;
                    }
                    Volatile.Write(ref _pendingAddsCount, 0);
                }

                if (adds is not null)
                {
                    Actions.AddRange(adds);
                    adds.Clear();
                    using (_pendingAddsLock.EnterScope())
                        _pendingAddsSpare ??= adds;
                }
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

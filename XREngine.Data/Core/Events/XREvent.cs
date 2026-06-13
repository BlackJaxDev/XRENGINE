using System.Runtime.CompilerServices;
using System.Threading;

namespace XREngine.Data.Core
{
    /// <summary>
    /// Event system for XR.
    /// </summary>
    public class XREvent : XREventBase<Action>
    {
        public static Func<string, IDisposable>? ProfilingHook = null;
        public static Func<bool>? IsProfilingEnabledHook = null;
        public static Func<object?>? CaptureLinkedProfilingContextHook = null;
        public static Func<object?, string, IDisposable>? LinkedProfilingHook = null;

        public XREvent() { }

        public List<XRPersistentCall>? PersistentCalls { get; set; }

        public bool HasPersistentCalls => PersistentCalls is { Count: > 0 };

        protected override IDisposable? BeginProfiling(string name)
            => ProfilingHook?.Invoke(name);

        protected override bool HasProfilingHooks
            => (IsProfilingEnabledHook?.Invoke() ?? true)
            && (ProfilingHook is not null || LinkedProfilingHook is not null);

        protected override object? CaptureLinkedProfilingContext()
            => CaptureLinkedProfilingContextHook?.Invoke();

        protected override IDisposable? BeginLinkedProfiling(object? context, string name)
            => LinkedProfilingHook?.Invoke(context, name) ?? BeginProfiling(name);

        public void Invoke()
        {
            WithProfiling("XREvent.Invoke", InvokeInternal);
        }

        private void InvokeInternal()
        {
            using (BeginProfiling("XREvent.ConsumeQueues"))
            {
                ConsumeQueues("XREvent.ConsumeQueues");
            }
            using (BeginProfiling("XREvent.Actions"))
            {
                for (int i = 0; i < Actions.Count; i++)
                {
                    using var listenerSample = BeginListenerProfiling("XREvent.Action", Actions[i], i);
                    Actions[i].Invoke();
                }
            }
            using (BeginProfiling("XREvent.PersistentCalls"))
            {
                InvokePersistentCalls([]);
            }
        }

        private void InvokePersistentCalls(object?[] args)
        {
            var calls = PersistentCalls;
            if (calls is null || calls.Count == 0)
                return;

            // Avoid invalid enumeration if edited while running.
            var snapshot = calls.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                var call = snapshot[i];
                if (call is null || !call.IsConfigured)
                    continue;

                if (!XRObjectBase.ObjectsCache.TryGetValue(call.TargetObjectId, out var targetObj))
                    continue;

                call.TryInvoke(targetObj, args);
            }
        }

        public async Task InvokeAsync()
        {
            await WithProfilingAsync("XREvent.InvokeAsync", InvokeAsyncInternal);
        }

        private async Task InvokeAsyncInternal()
        {
            using (BeginProfiling("XREvent.ConsumeQueues"))
            {
                ConsumeQueues("XREvent.ConsumeQueues");
            }

            var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
            if (snapshot is not null)
            {
                object? profilingContext = CaptureLinkedProfilingContext();
                using (BeginProfiling("XREvent.AsyncActions"))
                {
                    var tasks = new Task[snapshot.Length];
                    for (int i = 0; i < snapshot.Length; i++)
                    {
                        Action action = snapshot[i];
                        int index = i;
                        tasks[i] = Task.Run(() => InvokeLinkedListener(action, index, profilingContext, "XREvent.AsyncAction"));
                    }

                    await Task.WhenAll(tasks);
                }
            }

            using (BeginProfiling("XREvent.PersistentCalls"))
            {
                InvokePersistentCalls([]);
            }
        }

        public void InvokeParallel()
            => InvokeParallel(minParallelListeners: 1);

        public void InvokeParallel(int minParallelListeners)
        {
            var sample = BeginProfiling("XREvent.InvokeParallel");
            if (sample is null)
            {
                InvokeParallelInternal(minParallelListeners);
                return;
            }

            using (sample)
                InvokeParallelInternal(minParallelListeners);
        }

        private void InvokeParallelInternal(int minParallelListeners)
        {
            using (BeginProfiling("XREvent.ConsumeQueues"))
            {
                ConsumeQueues("XREvent.ConsumeQueues");
            }

            if (Actions.Count < Math.Max(2, minParallelListeners))
            {
                using var actionsSample = BeginProfiling("XREvent.Actions");
                for (int i = 0; i < Actions.Count; i++)
                {
                    using var listenerSample = BeginListenerProfiling("XREvent.Action", Actions[i], i);
                    Actions[i].Invoke();
                }
            }
            else
            {
                object? profilingContext = CaptureLinkedProfilingContext();
                using var actionsSample = BeginProfiling("XREvent.ParallelActions");
                Parallel.For(0, Actions.Count, i =>
                    InvokeLinkedListener(Actions[i], i, profilingContext, "XREvent.ParallelAction"));
            }

            using (BeginProfiling("XREvent.PersistentCalls"))
            {
                InvokePersistentCalls([]);
            }
        }

        private void InvokeLinkedListener(Action listener, int index, object? profilingContext, string prefix)
        {
            using var listenerSample = BeginLinkedListenerProfiling(profilingContext, prefix, listener, index);
            listener.Invoke();
        }

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
            if (e.Count == 0 && !e.HasPendingAdds && !e.HasPendingRemoves && !e.HasPersistentCalls)
                return null;
            return e;
        }
    }
    /// <summary>
    /// Event system for XR, with a single argument.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class XREvent<T> : XREventBase<Action<T>>
    {
        public static Func<string, IDisposable>? ProfilingHook = null;

        public XREvent() { }

        public List<XRPersistentCall>? PersistentCalls { get; set; }

        public bool HasPersistentCalls => PersistentCalls is { Count: > 0 };

        protected override IDisposable? BeginProfiling(string name)
            => ProfilingHook?.Invoke(name) ?? XREvent.ProfilingHook?.Invoke(name);

        protected override bool HasProfilingHooks
            => (XREvent.IsProfilingEnabledHook?.Invoke() ?? true)
            && (ProfilingHook is not null || XREvent.ProfilingHook is not null || XREvent.LinkedProfilingHook is not null);

        protected override object? CaptureLinkedProfilingContext()
            => XREvent.CaptureLinkedProfilingContextHook?.Invoke();

        protected override IDisposable? BeginLinkedProfiling(object? context, string name)
            => XREvent.LinkedProfilingHook?.Invoke(context, name) ?? BeginProfiling(name);

        public void Invoke(T item)
        {
            WithProfiling("XREvent<T>.Invoke", () =>
            {
                using (BeginProfiling("XREvent<T>.ConsumeQueues"))
                {
                    ConsumeQueues("XREvent<T>.ConsumeQueues");
                }

                using (BeginProfiling("XREvent<T>.Actions"))
                {
                    for (int i = 0; i < Actions.Count; i++)
                    {
                        using var listenerSample = BeginListenerProfiling("XREvent<T>.Action", Actions[i], i);
                        Actions[i].Invoke(item);
                    }
                }

                using (BeginProfiling("XREvent<T>.PersistentCalls"))
                {
                    InvokePersistentCalls(item);
                }
            });
        }

        public async Task InvokeAsync(T item)
        {
            await WithProfilingAsync("XREvent<T>.InvokeAsync", async () =>
            {
                using (BeginProfiling("XREvent<T>.ConsumeQueues"))
                {
                    ConsumeQueues("XREvent<T>.ConsumeQueues");
                }

                var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
                if (snapshot is not null)
                {
                    object? profilingContext = CaptureLinkedProfilingContext();
                    using (BeginProfiling("XREvent<T>.AsyncActions"))
                    {
                        var tasks = new Task[snapshot.Length];
                        for (int i = 0; i < snapshot.Length; i++)
                        {
                            Action<T> action = snapshot[i];
                            int index = i;
                            tasks[i] = Task.Run(() => InvokeLinkedListener(action, item, index, profilingContext, "XREvent<T>.AsyncAction"));
                        }

                        await Task.WhenAll(tasks);
                    }
                }

                using (BeginProfiling("XREvent<T>.PersistentCalls"))
                {
                    InvokePersistentCalls(item);
                }
            });
        }

        private void InvokeLinkedListener(Action<T> listener, T item, int index, object? profilingContext, string prefix)
        {
            using var listenerSample = BeginLinkedListenerProfiling(profilingContext, prefix, listener, index);
            listener.Invoke(item);
        }

        private void InvokePersistentCalls(T item)
        {
            var calls = PersistentCalls;
            if (calls is null || calls.Count == 0)
                return;

            object?[] singleArg = [item];

            // If the selected method uses tuple expansion, unpack ValueTuple payloads.
            // Otherwise pass the single payload as-is.
            if (item is ITuple tuple)
            {
                // We may still need the non-expanded path for callers, but the per-call flag is stored on the call itself.
                // Allocate both forms and choose per call.
                object?[] expanded = new object?[tuple.Length];
                for (int i = 0; i < tuple.Length; i++)
                    expanded[i] = tuple[i];

                var snapshot = calls.ToArray();
                for (int i = 0; i < snapshot.Length; i++)
                {
                    var call = snapshot[i];
                    if (call is null || !call.IsConfigured)
                        continue;

                    if (!XRObjectBase.ObjectsCache.TryGetValue(call.TargetObjectId, out var targetObj))
                        continue;

                    call.TryInvoke(targetObj, call.UseTupleExpansion ? expanded : singleArg);
                }

                return;
            }

            var defaultSnapshot = calls.ToArray();
            for (int i = 0; i < defaultSnapshot.Length; i++)
            {
                var call = defaultSnapshot[i];
                if (call is null || !call.IsConfigured)
                    continue;

                if (!XRObjectBase.ObjectsCache.TryGetValue(call.TargetObjectId, out var targetObj))
                    continue;

                call.TryInvoke(targetObj, singleArg);
            }
        }

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
            if (e.Count == 0 && !e.HasPendingAdds && !e.HasPendingRemoves && !e.HasPersistentCalls)
                return null;
            return e;
        }
    }
}

using System.Runtime.CompilerServices;
using System.Threading;
using System.Runtime.ExceptionServices;

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
            IDisposable? sample = BeginProfiling("XREvent.Invoke");
            if (sample is null)
            {
                InvokeInternal();
                return;
            }

            using (sample)
                InvokeInternal();
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
                InvokePersistentCalls();
            }
        }

        private void InvokePersistentCalls()
        {
            var calls = PersistentCalls;
            if (calls is null || calls.Count == 0)
                return;

            for (int i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                if (call is null || !call.IsConfigured)
                    continue;

                if (!XRObjectBase.ObjectsCache.TryGetValue(call.TargetObjectId, out var targetObj))
                    continue;

                call.TryInvokeZeroArg(targetObj);
            }
        }

        /// <summary>
        /// Runs independent, thread-safe listeners concurrently and completes after all listeners finish.
        /// Persistent calls remain ordered and run after the concurrent listeners.
        /// </summary>
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
                using (BeginProfiling("XREvent.AsyncActions"))
                {
                    object? profilingContext = CaptureLinkedProfilingContext();
                    if (OperatingSystem.IsBrowser())
                    {
                        ExceptionDispatchInfo? firstFault = null;
                        for (int i = 0; i < snapshot.Length; i++)
                        {
                            try
                            {
                                InvokeLinkedListener(snapshot[i], i, profilingContext, "XREvent.AsyncAction");
                            }
                            catch (Exception error)
                            {
                                firstFault ??= ExceptionDispatchInfo.Capture(error);
                            }
                        }
                        firstFault?.Throw();
                    }
                    else
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
            }

            using (BeginProfiling("XREvent.PersistentCalls"))
            {
                InvokePersistentCalls();
            }
        }

        /// <summary>
        /// Runs independent, thread-safe listeners concurrently and blocks until all listeners finish.
        /// </summary>
        public void InvokeParallel()
            => InvokeParallel(minParallelListeners: 1);

        public void InvokeParallel(int minParallelListeners)
        {
            if (OperatingSystem.IsBrowser())
                throw new PlatformNotSupportedException("Concurrent event invocation requires desktop worker threads. Use Invoke or InvokeAsync in a browser world.");
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
                using var actionsSample = BeginProfiling("XREvent.ParallelActions");
                object? profilingContext = CaptureLinkedProfilingContext();
                Parallel.For(0, Actions.Count, i =>
                    InvokeLinkedListener(Actions[i], i, profilingContext, "XREvent.ParallelAction"));
            }

            using (BeginProfiling("XREvent.PersistentCalls"))
            {
                InvokePersistentCalls();
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
            IDisposable? sample = BeginProfiling("XREvent<T>.Invoke");
            if (sample is null)
            {
                InvokeInternal(item);
                return;
            }

            using (sample)
                InvokeInternal(item);
        }

        private void InvokeInternal(T item)
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
        }

        /// <summary>
        /// Runs independent, thread-safe listeners concurrently and completes after all listeners finish.
        /// Persistent calls remain ordered and run after the concurrent listeners.
        /// </summary>
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
                    using (BeginProfiling("XREvent<T>.AsyncActions"))
                    {
                        object? profilingContext = CaptureLinkedProfilingContext();
                        if (OperatingSystem.IsBrowser())
                        {
                            ExceptionDispatchInfo? firstFault = null;
                            for (int i = 0; i < snapshot.Length; i++)
                            {
                                try
                                {
                                    InvokeLinkedListener(snapshot[i], item, i, profilingContext, "XREvent<T>.AsyncAction");
                                }
                                catch (Exception error)
                                {
                                    firstFault ??= ExceptionDispatchInfo.Capture(error);
                                }
                            }
                            firstFault?.Throw();
                        }
                        else
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

            object?[]? expanded = null;
            for (int i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                if (call is null || !call.IsConfigured)
                    continue;

                if (!XRObjectBase.ObjectsCache.TryGetValue(call.TargetObjectId, out var targetObj))
                    continue;

                if (call.UseTupleExpansion && item is ITuple tuple)
                {
                    if (expanded is null)
                    {
                        expanded = new object?[tuple.Length];
                        for (int j = 0; j < tuple.Length; j++)
                            expanded[j] = tuple[j];
                    }

                    call.TryInvoke(targetObj, expanded);
                }
                else
                {
                    call.TryInvokeTyped(targetObj, item);
                }
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

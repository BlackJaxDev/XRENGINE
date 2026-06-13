namespace XREngine.Data.Core
{
    /// <summary>
    /// Event system for XR that returns a bool from its listeners.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class XRBoolEvent<T> : XREventBase<Func<T, bool>>
    {
        public static Func<string, IDisposable>? ProfilingHook = null;

        public XRBoolEvent() { }

        protected override IDisposable? BeginProfiling(string name)
            => ProfilingHook?.Invoke(name) ?? XREvent.ProfilingHook?.Invoke(name);

        protected override bool HasProfilingHooks
            => (XREvent.IsProfilingEnabledHook?.Invoke() ?? true)
            && (ProfilingHook is not null || XREvent.ProfilingHook is not null || XREvent.LinkedProfilingHook is not null);

        protected override object? CaptureLinkedProfilingContext()
            => XREvent.CaptureLinkedProfilingContextHook?.Invoke();

        protected override IDisposable? BeginLinkedProfiling(object? context, string name)
            => XREvent.LinkedProfilingHook?.Invoke(context, name) ?? BeginProfiling(name);

        /// <summary>
        /// Invokes all listeners and returns true if all return true.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool InvokeAllMatch(T item)
        {
            bool result = false;
            WithProfiling("XRBoolEvent<T>.InvokeAllMatch", () =>
            {
                using (BeginProfiling("XRBoolEvent<T>.ConsumeQueues"))
                {
                    ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                }

                result = true;
                using (BeginProfiling("XRBoolEvent<T>.Actions"))
                {
                    for (int i = 0; i < Actions.Count; i++)
                    {
                        using var listenerSample = BeginListenerProfiling("XRBoolEvent<T>.Action", Actions[i], i);
                        if (!Actions[i].Invoke(item))
                        {
                            result = false;
                            break;
                        }
                    }
                }
            });
            return result;
        }

        /// <summary>
        /// Invokes all listeners concurrently and returns true if all return true.
        /// </summary>
        public async Task<bool> InvokeAllMatchAsync(T item)
        {
            bool result = false;
            await WithProfilingAsync("XRBoolEvent<T>.InvokeAllMatchAsync", async () =>
            {
                using (BeginProfiling("XRBoolEvent<T>.ConsumeQueues"))
                {
                    ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                }

                var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
                if (snapshot is null)
                {
                    result = true;
                    return;
                }

                object? profilingContext = CaptureLinkedProfilingContext();
                var tasks = new Task<bool>[snapshot.Length];
                using (BeginProfiling("XRBoolEvent<T>.AsyncActions"))
                {
                    for (int i = 0; i < snapshot.Length; i++)
                    {
                        Func<T, bool> listener = snapshot[i];
                        int index = i;
                        tasks[i] = Task.Run(() => InvokeLinkedListener(listener, item, index, profilingContext, "XRBoolEvent<T>.AsyncAction"));
                    }

                    await Task.WhenAll(tasks);
                }

                var results = new bool[tasks.Length];
                for (int i = 0; i < tasks.Length; i++)
                    results[i] = tasks[i].Result;
                result = results.All(static x => x);
            });
            return result;
        }

        /// <summary>
        /// Invokes all listeners in parallel and returns true if all return true.
        /// </summary>
        public bool InvokeAllMatchParallel(T item)
        {
            bool result = false;
            WithProfiling("XRBoolEvent<T>.InvokeAllMatchParallel", () =>
            {
                using (BeginProfiling("XRBoolEvent<T>.ConsumeQueues"))
                {
                    ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                }

                var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
                if (snapshot is null)
                {
                    result = true;
                    return;
                }

                int allTrue = 1;
                object? profilingContext = CaptureLinkedProfilingContext();
                using var actionsSample = BeginProfiling("XRBoolEvent<T>.ParallelActions");
                Parallel.For(0, snapshot.Length, (i, state) =>
                {
                    if (Volatile.Read(ref allTrue) == 0)
                    {
                        state.Stop();
                        return;
                    }

                    if (!InvokeLinkedListener(snapshot[i], item, i, profilingContext, "XRBoolEvent<T>.ParallelAction"))
                    {
                        Interlocked.Exchange(ref allTrue, 0);
                        state.Stop();
                    }
                });

                result = Volatile.Read(ref allTrue) != 0;
            });
            return result;
        }

        /// <summary>
        /// Invokes listeners in order and returns true once one returns true.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool InvokeAnyMatch(T item)
        {
            bool result = false;
            WithProfiling("XRBoolEvent<T>.InvokeAnyMatch", () =>
            {
                using (BeginProfiling("XRBoolEvent<T>.ConsumeQueues"))
                {
                    ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                }

                using (BeginProfiling("XRBoolEvent<T>.Actions"))
                {
                    for (int i = 0; i < Actions.Count; i++)
                    {
                        using var listenerSample = BeginListenerProfiling("XRBoolEvent<T>.Action", Actions[i], i);
                        if (Actions[i].Invoke(item))
                        {
                            result = true;
                            break;
                        }
                    }
                }
            });
            return result;
        }

        /// <summary>
        /// Invokes all listeners concurrently and returns true once one returns true.
        /// </summary>
        public async Task<bool> InvokeAnyMatchAsync(T item)
        {
            bool result = false;
            await WithProfilingAsync("XRBoolEvent<T>.InvokeAnyMatchAsync", async () =>
            {
                using (BeginProfiling("XRBoolEvent<T>.ConsumeQueues"))
                {
                    ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                }

                var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
                if (snapshot is null)
                {
                    result = false;
                    return;
                }

                object? profilingContext = CaptureLinkedProfilingContext();
                var tasks = new List<Task<bool>>(snapshot.Length);
                using (BeginProfiling("XRBoolEvent<T>.AsyncActions"))
                {
                    for (int i = 0; i < snapshot.Length; i++)
                    {
                        Func<T, bool> listener = snapshot[i];
                        int index = i;
                        tasks.Add(Task.Run(() => InvokeLinkedListener(listener, item, index, profilingContext, "XRBoolEvent<T>.AsyncAction")));
                    }

                    while (tasks.Count > 0)
                    {
                        var completed = await Task.WhenAny(tasks);
                        tasks.Remove(completed);
                        if (await completed)
                        {
                            result = true;
                            return;
                        }
                    }
                }

                result = false;
            });
            return result;
        }

        /// <summary>
        /// Invokes all listeners in parallel and returns true once one returns true.
        /// </summary>
        public bool InvokeAnyMatchParallel(T item)
        {
            bool result = false;
            WithProfiling("XRBoolEvent<T>.InvokeAnyMatchParallel", () =>
            {
                using (BeginProfiling("XRBoolEvent<T>.ConsumeQueues"))
                {
                    ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                }

                var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
                if (snapshot is null)
                {
                    result = false;
                    return;
                }

                int anyTrue = 0;
                object? profilingContext = CaptureLinkedProfilingContext();
                using var actionsSample = BeginProfiling("XRBoolEvent<T>.ParallelActions");
                Parallel.For(0, snapshot.Length, (i, state) =>
                {
                    if (Volatile.Read(ref anyTrue) != 0)
                    {
                        state.Stop();
                        return;
                    }

                    if (InvokeLinkedListener(snapshot[i], item, i, profilingContext, "XRBoolEvent<T>.ParallelAction"))
                    {
                        Interlocked.Exchange(ref anyTrue, 1);
                        state.Stop();
                    }
                });

                result = Volatile.Read(ref anyTrue) != 0;
            });
            return result;
        }

        private bool InvokeLinkedListener(Func<T, bool> listener, T item, int index, object? profilingContext, string prefix)
        {
            using var listenerSample = BeginLinkedListenerProfiling(profilingContext, prefix, listener, index);
            return listener.Invoke(item);
        }

        public static XRBoolEvent<T>? operator +(XRBoolEvent<T>? e, Func<T, bool> a)
        {
            e ??= new();
            e.AddListener(a);
            return e;
        }
        public static XRBoolEvent<T>? operator -(XRBoolEvent<T>? e, Func<T, bool> a)
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

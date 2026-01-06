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
            => ProfilingHook?.Invoke(name);

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
                ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                result = Actions.All(x => x.Invoke(item));
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
                ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
                if (snapshot is null)
                {
                    result = true;
                    return;
                }

                var results = await Task.WhenAll(snapshot.Select(a => Task.Run(() => a.Invoke(item))));
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
                ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
                if (snapshot is null)
                {
                    result = true;
                    return;
                }

                int allTrue = 1;
                Parallel.ForEach(snapshot, (listener, state) =>
                {
                    if (Volatile.Read(ref allTrue) == 0)
                    {
                        state.Stop();
                        return;
                    }

                    if (!listener.Invoke(item))
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
                ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                result = Actions.Any(x => x.Invoke(item));
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
                ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
                if (snapshot is null)
                {
                    result = false;
                    return;
                }

                var tasks = snapshot.Select(a => Task.Run(() => a.Invoke(item))).ToList();
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
                ConsumeQueues("XRBoolEvent<T>.ConsumeQueues");
                var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
                if (snapshot is null)
                {
                    result = false;
                    return;
                }

                int anyTrue = 0;
                Parallel.ForEach(snapshot, (listener, state) =>
                {
                    if (Volatile.Read(ref anyTrue) != 0)
                    {
                        state.Stop();
                        return;
                    }

                    if (listener.Invoke(item))
                    {
                        Interlocked.Exchange(ref anyTrue, 1);
                        state.Stop();
                    }
                });

                result = Volatile.Read(ref anyTrue) != 0;
            });
            return result;
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

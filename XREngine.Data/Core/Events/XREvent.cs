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

        public XREvent() { }

        public List<XRPersistentCall>? PersistentCalls { get; set; }

        public bool HasPersistentCalls => PersistentCalls is { Count: > 0 };

        protected override IDisposable? BeginProfiling(string name)
            => ProfilingHook?.Invoke(name);

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
                Actions.ForEach(x => x.Invoke());
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
            ConsumeQueues("XREvent.ConsumeQueues");
            var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
            if (snapshot is not null)
                await Task.WhenAll(snapshot.Select(Task.Run));
            InvokePersistentCalls([]);
        }

        public void InvokeParallel()
        {
            WithProfiling("XREvent.InvokeParallel", InvokeParallelInternal);
        }

        private void InvokeParallelInternal()
        {
            ConsumeQueues("XREvent.ConsumeQueues");
            Parallel.ForEach(Actions, x => x.Invoke());
            InvokePersistentCalls([]);
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
            => ProfilingHook?.Invoke(name);

        public void Invoke(T item)
        {
            WithProfiling("XREvent<T>.Invoke", () =>
            {
                ConsumeQueues("XREvent<T>.ConsumeQueues");
                Actions.ForEach(x => x.Invoke(item));
                InvokePersistentCalls(item);
            });
        }

        public async Task InvokeAsync(T item)
        {
            await WithProfilingAsync("XREvent<T>.InvokeAsync", async () =>
            {
                ConsumeQueues("XREvent<T>.ConsumeQueues");
                var snapshot = Actions.Count == 0 ? null : Actions.ToArray();
                if (snapshot is not null)
                    await Task.WhenAll(snapshot.Select(a => Task.Run(() => a.Invoke(item))));
                InvokePersistentCalls(item);
            });
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

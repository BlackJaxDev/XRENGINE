using XREngine.Data.Runtime.AotParity;
using System.Collections.Concurrent;
using System.Diagnostics;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Owns backend-neutral root, play-state, and tick lifecycle state for one
/// world context; rendering state is owned separately.
/// </summary>
public sealed partial class RuntimeWorldLifecycle
{
    private readonly Dictionary<ETickGroup, TickGroupQueues> _ticks = [];
    private volatile bool _ticksReleased;

    public RuntimeWorldLifecycle(
        IRuntimeWorldContext worldContext,
        Action<SceneNode>? onRootNodeDestroying = null,
        Func<SceneNode, bool>? participatesInPlay = null)
    {
        RootNodes = new RootNodeCollection(worldContext, onRootNodeDestroying, participatesInPlay);
        foreach (ETickGroup group in Enum.GetValues<ETickGroup>())
            _ticks[group] = new TickGroupQueues(group);
    }

    private int _playState;
    public RuntimeWorldPlayState PlayState
    {
        get => (RuntimeWorldPlayState)Volatile.Read(ref _playState);
        set => Volatile.Write(ref _playState, (int)value);
    }
    public RootNodeCollection RootNodes { get; }

    public bool IsPlaySessionActive
        => PlayState is RuntimeWorldPlayState.BeginningPlay
            or RuntimeWorldPlayState.Playing
            or RuntimeWorldPlayState.Paused;

    public bool TransitioningPlay
        => PlayState is RuntimeWorldPlayState.BeginningPlay
            or RuntimeWorldPlayState.EndingPlay;

    public void RegisterTick(ETickGroup group, int order, WorldTick callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        GetTickQueue(group, order)?.Enqueue(add: true, callback);
    }

    public void UnregisterTick(ETickGroup group, int order, WorldTick callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        GetTickQueue(group, order)?.Enqueue(add: false, callback);
    }

    /// <summary>
    /// Ends callback ownership without dispatching pending changes. Existing
    /// dispatch snapshots may finish, but additions cannot reopen these queues.
    /// </summary>
    internal void ReleaseTicks()
    {
        _ticksReleased = true;
        foreach (TickGroupQueues queues in _ticks.Values)
            queues.Release();
    }

    /// <summary>
    /// Dispatches the group's ordered queues without locking, copying or
    /// allocating. Callbacks may register and unregister while the group runs:
    /// changes wait in the queues' pending lists until their next dispatch, and
    /// a queue created meanwhile joins the next dispatch of the group.
    /// </summary>
    public void TickGroup(ETickGroup group)
    {
        using var parityScope = IsPlaySessionActive
            ? AotParityDiagnostics.EnterSynchronousPlayerPath(EAotParityPlayerPathKind.PlayMode) : default;
        if (_ticksReleased || !_ticks.TryGetValue(group, out TickGroupQueues? queues))
            return;

        if (RuntimeWorldTickTelemetry.Enabled)
        {
            DispatchObserved(group, queues);
            return;
        }

        TickQueue[] ordered = queues.Ordered;
        for (int index = 0; index < ordered.Length && !_ticksReleased; ++index)
            ordered[index].Dispatch();
    }

    /// <summary>The same dispatch as <see cref="TickGroup"/>, timed for the tick counters.</summary>
    private void DispatchObserved(ETickGroup group, TickGroupQueues queues)
    {
        long started = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        TickQueue[] ordered = queues.Ordered;
        long snapshotFinished = Stopwatch.GetTimestamp();
        try
        {
            for (int index = 0; index < ordered.Length && !_ticksReleased; ++index)
                ordered[index].Dispatch();
        }
        finally
        {
            RuntimeWorldTickTelemetry.GroupDispatched(
                group,
                started,
                snapshotFinished - started,
                Stopwatch.GetTimestamp() - started,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
    }

    private TickQueue? GetTickQueue(ETickGroup group, int order)
    {
        if (_ticksReleased)
            return null;
        if (!_ticks.TryGetValue(group, out TickGroupQueues? queues))
            throw new ArgumentOutOfRangeException(nameof(group));

        return queues.GetOrAdd(order);
    }

    /// <summary>
    /// The ordered queues of one tick group. A queue is created by the first
    /// registration of its order value and is never removed, so each creation
    /// publishes a new ordered array and dispatch reads the current one without
    /// the lock or an enumerator.
    /// </summary>
    private sealed class TickGroupQueues(ETickGroup group)
    {
        private readonly SortedDictionary<int, TickQueue> _byOrder = [];
        private TickQueue[] _ordered = [];
        private bool _released;

        /// <summary>The queues in ascending order value; replaced, never mutated.</summary>
        public TickQueue[] Ordered => Volatile.Read(ref _ordered);

        public TickQueue? GetOrAdd(int order)
        {
            lock (_byOrder)
            {
                if (_released)
                    return null;
                if (_byOrder.TryGetValue(order, out TickQueue? queue))
                    return queue;

                queue = new TickQueue(group, order);
                _byOrder.Add(order, queue);
                TickQueue[] ordered = new TickQueue[_byOrder.Count];
                _byOrder.Values.CopyTo(ordered, 0);
                Volatile.Write(ref _ordered, ordered);
                return queue;
            }
        }

        public void Release()
        {
            lock (_byOrder)
            {
                _released = true;
                _byOrder.Clear();
                Volatile.Write(ref _ordered, []);
            }
        }
    }

    private sealed class TickQueue(ETickGroup group, int order)
    {
        private readonly List<WorldTick> _callbacks = [];
        private readonly ConcurrentQueue<(bool Add, WorldTick Callback)> _pending = [];

        // Parallel to _callbacks. Exists only while tick observation is enabled.
        private readonly List<RuntimeWorldTickCallbackTelemetry>? _callbackTelemetry =
            RuntimeWorldTickTelemetry.Enabled ? [] : null;

        public void Enqueue(bool add, WorldTick callback)
        {
            if (_callbackTelemetry is not null)
                RuntimeWorldTickTelemetry.RegistrationRequested(add);
            _pending.Enqueue((add, callback));
        }

        public void Dispatch()
        {
            if (_callbackTelemetry is not null)
            {
                DispatchObserved(_callbackTelemetry);
                return;
            }

            ApplyPending();
            if (RuntimeComponentTickTiming.Recorder is { } recorder)
            {
                DispatchTimed(recorder);
                return;
            }

            for (int index = 0; index < _callbacks.Count; ++index)
                _callbacks[index]();
        }

        /// <summary>The unobserved dispatch with each callback timed for per-component tick timing.</summary>
        private void DispatchTimed(IRuntimeComponentTickTimingRecorder recorder)
        {
            for (int index = 0; index < _callbacks.Count; ++index)
            {
                WorldTick callback = _callbacks[index];
                long started = Stopwatch.GetTimestamp();
                callback();
                RecordOwnerTime(recorder, callback, Stopwatch.GetTimestamp() - started);
            }
        }

        private void RecordOwnerTime(IRuntimeComponentTickTimingRecorder recorder, WorldTick callback, long elapsed)
        {
            if (RuntimeTickCallbackOwner.Resolve(callback) is { } owner)
                recorder.RecordComponentTick(owner, group, elapsed);
        }

        /// <summary>
        /// Same order and membership rules as the unobserved path, with the
        /// pending application and every callback timed.
        /// </summary>
        private void DispatchObserved(List<RuntimeWorldTickCallbackTelemetry> callbackTelemetry)
        {
            long pendingStarted = Stopwatch.GetTimestamp();
            long observerTicks = 0L;
            int adds = 0;
            int duplicateAdds = 0;
            int removes = 0;
            int missingRemoves = 0;
            long membershipComparisons = 0L;
            while (_pending.TryDequeue(out (bool Add, WorldTick Callback) change))
            {
                int existing = _callbacks.IndexOf(change.Callback);
                membershipComparisons += existing >= 0 ? existing + 1 : _callbacks.Count;
                if (change.Add)
                {
                    if (existing >= 0)
                    {
                        ++duplicateAdds;
                        continue;
                    }

                    // Identifying the callback is observer work; keep it out of the
                    // pending-application time the engine itself would spend.
                    long resolveStarted = Stopwatch.GetTimestamp();
                    RuntimeWorldTickCallbackTelemetry telemetry =
                        RuntimeWorldTickTelemetry.ResolveCallback(group, order, change.Callback);
                    observerTicks += Stopwatch.GetTimestamp() - resolveStarted;
                    telemetry.InstanceRegistered();
                    _callbacks.Add(change.Callback);
                    callbackTelemetry.Add(telemetry);
                    ++adds;
                }
                else if (existing >= 0)
                {
                    callbackTelemetry[existing].InstanceRemoved();
                    _callbacks.RemoveAt(existing);
                    callbackTelemetry.RemoveAt(existing);
                    ++removes;
                }
                else
                {
                    ++missingRemoves;
                }
            }

            long callbacksStarted = Stopwatch.GetTimestamp();
            int invoked = 0;
            IRuntimeComponentTickTimingRecorder? recorder = RuntimeComponentTickTiming.Recorder;
            for (int index = 0; index < _callbacks.Count; ++index)
            {
                RuntimeWorldTickCallbackTelemetry telemetry = callbackTelemetry[index];
                WorldTick callback = _callbacks[index];
                int collectionsBefore = GC.CollectionCount(0);
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                callback();
                long elapsed = Stopwatch.GetTimestamp() - started;
                telemetry.RecordInvocation(
                    started,
                    elapsed,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                    GC.CollectionCount(0) != collectionsBefore);
                if (recorder is not null)
                    RecordOwnerTime(recorder, callback, elapsed);
                ++invoked;
            }

            RuntimeWorldTickTelemetry.QueueDispatched(
                group,
                pendingStarted,
                callbacksStarted - pendingStarted - observerTicks,
                observerTicks,
                Stopwatch.GetTimestamp() - callbacksStarted,
                invoked,
                adds,
                duplicateAdds,
                removes,
                missingRemoves,
                membershipComparisons,
                _callbacks.Count);
        }

        private void ApplyPending()
        {
            while (_pending.TryDequeue(out (bool Add, WorldTick Callback) change))
            {
                if (change.Add)
                {
                    if (!_callbacks.Contains(change.Callback))
                        _callbacks.Add(change.Callback);
                }
                else
                {
                    _callbacks.Remove(change.Callback);
                }
            }
        }
    }
}

public enum RuntimeWorldPlayState
{
    Stopped,
    BeginningPlay,
    Playing,
    EndingPlay,
    Paused,
}

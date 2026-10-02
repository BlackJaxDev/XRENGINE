using XREngine.Data.Runtime.AotParity;
using System.Buffers;
using System.Collections.Concurrent;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Owns backend-neutral root, play-state, and tick lifecycle state for one
/// world context; rendering state is owned separately.
/// </summary>
public sealed partial class RuntimeWorldLifecycle
{
    private readonly Dictionary<ETickGroup, SortedList<int, TickQueue>> _ticks = [];

    public RuntimeWorldLifecycle(
        IRuntimeWorldContext worldContext,
        Action<SceneNode>? onRootNodeDestroying = null,
        Func<SceneNode, bool>? participatesInPlay = null)
    {
        RootNodes = new RootNodeCollection(worldContext, onRootNodeDestroying, participatesInPlay);
        foreach (ETickGroup group in Enum.GetValues<ETickGroup>())
            _ticks[group] = [];
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
        GetTickQueue(group, order).Enqueue(add: true, callback);
    }

    public void UnregisterTick(ETickGroup group, int order, WorldTick callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        GetTickQueue(group, order).Enqueue(add: false, callback);
    }

    /// <summary>
    /// Dispatches callbacks from a pooled snapshot so callbacks may safely
    /// mutate registration while the group is executing.
    /// </summary>
    public void TickGroup(ETickGroup group)
    {
        using var parityScope = IsPlaySessionActive
            ? AotParityDiagnostics.EnterSynchronousPlayerPath(EAotParityPlayerPathKind.PlayMode) : default;
        if (!_ticks.TryGetValue(group, out SortedList<int, TickQueue>? ordered))
            return;

        TickQueue[] snapshot;
        int count;
        lock (ordered)
        {
            count = ordered.Count;
            if (count == 0)
                return;

            snapshot = ArrayPool<TickQueue>.Shared.Rent(count);
            IList<TickQueue> queues = ordered.Values;
            for (int index = 0; index < count; index++)
                snapshot[index] = queues[index];
        }

        try
        {
            for (int index = 0; index < count; ++index)
                snapshot[index].Dispatch();
        }
        finally
        {
            Array.Clear(snapshot, 0, count);
            ArrayPool<TickQueue>.Shared.Return(snapshot);
        }
    }

    private TickQueue GetTickQueue(ETickGroup group, int order)
    {
        if (!_ticks.TryGetValue(group, out SortedList<int, TickQueue>? ordered))
            _ticks[group] = ordered = [];

        lock (ordered)
        {
            if (!ordered.TryGetValue(order, out TickQueue? queue))
                ordered.Add(order, queue = new TickQueue());
            return queue;
        }
    }

    private sealed class TickQueue
    {
        private readonly List<WorldTick> _callbacks = [];
        private readonly ConcurrentQueue<(bool Add, WorldTick Callback)> _pending = [];

        public void Enqueue(bool add, WorldTick callback)
            => _pending.Enqueue((add, callback));

        public void Dispatch()
        {
            ApplyPending();
            for (int index = 0; index < _callbacks.Count; ++index)
                _callbacks[index]();
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

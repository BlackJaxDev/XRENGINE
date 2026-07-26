using System.Buffers;
using System.Collections.Concurrent;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Owns backend-neutral state for a production <c>XRWorldInstance</c>.
/// The facade instance delegates target-world, play-state, root-node, and tick
/// lifecycle operations here; rendering state is owned separately.
/// </summary>
public sealed class RuntimeWorldLifecycle
{
    private readonly Dictionary<ETickGroup, SortedDictionary<int, TickQueue>> _ticks = [];

    public RuntimeWorldLifecycle(
        IRuntimeWorldContext worldContext,
        Action<SceneNode>? onRootNodeDestroying = null)
    {
        RootNodes = new RootNodeCollection(worldContext, onRootNodeDestroying);
        foreach (ETickGroup group in Enum.GetValues<ETickGroup>())
            _ticks[group] = [];
    }

    public XRWorld? TargetWorld { get; set; }
    public RuntimeWorldPlayState PlayState { get; set; }
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
        if (!_ticks.TryGetValue(group, out SortedDictionary<int, TickQueue>? ordered))
            return;

        TickQueue[] snapshot = ArrayPool<TickQueue>.Shared.Rent(ordered.Count);
        int count = 0;
        lock (ordered)
        {
            foreach (TickQueue queue in ordered.Values)
                snapshot[count++] = queue;
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
        if (!_ticks.TryGetValue(group, out SortedDictionary<int, TickQueue>? ordered))
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

using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Retains a transition path from a selected state-machine asset.</summary>
internal sealed class InspectorFloatTransitionOwnership
{
    private readonly AnimStateMachine _machine;
    private readonly AnimLayer _layer;
    private readonly AnimStateBase _state;
    private readonly EventList<AnimLayer> _layers;
    private readonly EventList<AnimState>? _states;
    private readonly EventList<AnimStateTransition> _transitions;
    private readonly int _layerIndex;
    private readonly int _stateIndex;
    private readonly int _transitionIndex;
    private readonly int _layerCount;
    private readonly int _stateCount;
    private readonly int _transitionCount;
    private readonly bool _anyState;
    private readonly Guid _machineIdentity;
    private readonly Guid _documentIdentity;
    private readonly ObjectConstructionIdentity _machineLifetime;
    private readonly ObjectConstructionIdentity _documentLifetime;
    private readonly ObjectConstructionIdentity _layersLifetime;
    private readonly ObjectConstructionIdentity? _statesLifetime;
    private readonly ObjectConstructionIdentity _transitionsLifetime;

    private InspectorFloatTransitionOwnership(AnimStateMachine machine,
        EventList<AnimLayer> layers, int layerIndex, AnimLayer layer,
        EventList<AnimState>? states, int stateIndex, AnimStateBase state,
        EventList<AnimStateTransition> transitions, int transitionIndex,
        AnimStateTransition transition, bool anyState)
    {
        _machine = machine;
        _layer = layer;
        _state = state;
        _layers = layers;
        _states = states;
        _transitions = transitions;
        _layerIndex = layerIndex;
        _stateIndex = stateIndex;
        _transitionIndex = transitionIndex;
        _layerCount = layers.Count;
        _stateCount = states?.Count ?? 0;
        _transitionCount = transitions.Count;
        _anyState = anyState;
        _machineIdentity = machine.ID;
        Document = machine.SourceAsset;
        _documentIdentity = Document.ID;
        _machineLifetime = ObjectConstructionIdentity.Capture(machine);
        _documentLifetime = ObjectConstructionIdentity.Capture(Document);
        _layersLifetime = ObjectConstructionIdentity.Capture(layers);
        _statesLifetime = states is null ? null : ObjectConstructionIdentity.Capture(states);
        _transitionsLifetime = ObjectConstructionIdentity.Capture(transitions);
        Transition = transition;
        ReservationOwners = states is null
            ? [machine, Document, layers, layer, state, transitions, transition]
            : [machine, Document, layers, layer, states, state, transitions,
                transition];
    }

    public AnimStateTransition Transition { get; }
    public XRAsset Document { get; }
    public object[] ReservationOwners { get; }

    public static bool TryCapture(AnimStateMachine machine, int layerIndex,
        int stateIndex, int transitionIndex, bool anyState,
        out InspectorFloatTransitionOwnership? ownership, out string reason)
    {
        ownership = null;
        reason = "The selected state-machine path is unavailable.";
        if (machine.IsDestroyed || machine.IsDestroyQueued ||
            machine.Layers is not { IsDestroyed: false, IsDestroyQueued: false } layers ||
            (uint)layerIndex >= (uint)layers.Count ||
            layers[layerIndex] is not { } layer)
            return false;
        EventList<AnimState>? states = null;
        AnimStateBase state;
        if (anyState)
        {
            state = layer.AnyState;
            stateIndex = -1;
        }
        else
        {
            states = layer.States;
            if (states is null || states.IsDestroyed || states.IsDestroyQueued ||
                (uint)stateIndex >= (uint)states.Count ||
                states[stateIndex] is not { } selected)
                return false;
            state = selected;
        }
        if (state.Transitions is not
                { IsDestroyed: false, IsDestroyQueued: false } transitions ||
            (uint)transitionIndex >= (uint)transitions.Count ||
            transitions[transitionIndex] is not { } transition ||
            UniqueIndex(layers, layer) != layerIndex ||
            !anyState && UniqueIndex(states!, (AnimState)state) != stateIndex ||
            UniqueIndex(transitions, transition) != transitionIndex)
            return false;
        ownership = new(machine, layers, layerIndex, layer, states, stateIndex,
            state, transitions, transitionIndex, transition, anyState);
        if (!ownership.IsCurrent())
        {
            ownership = null;
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public bool IsCurrent()
        => IsCurrentFast() &&
            UniqueIndex(_layers, _layer) == _layerIndex &&
            (_anyState || UniqueIndex(_states!, (AnimState)_state) == _stateIndex) &&
            UniqueIndex(_transitions, Transition) == _transitionIndex;

    public bool IsCurrentFast()
        => !_machine.IsDestroyed && !_machine.IsDestroyQueued &&
            _machine.ID == _machineIdentity &&
            !Document.IsDestroyed && !Document.IsDestroyQueued &&
            Document.ID == _documentIdentity &&
            _machineLifetime.IsCurrent && _documentLifetime.IsCurrent &&
            _layersLifetime.IsCurrent &&
            (_statesLifetime?.IsCurrent ?? true) &&
            _transitionsLifetime.IsCurrent &&
            ReferenceEquals(_machine.SourceAsset, Document) &&
            ReferenceEquals(_machine.Layers, _layers) &&
            HasAt(_layers, _layerCount, _layerIndex, _layer) &&
            (_anyState
                ? ReferenceEquals(_layer.AnyState, _state)
                : ReferenceEquals(_layer.States, _states) &&
                    HasAt(_states, _stateCount, _stateIndex, (AnimState)_state)) &&
            ReferenceEquals(_state.Transitions, _transitions) &&
            HasAt(_transitions, _transitionCount, _transitionIndex, Transition);

    private static bool HasAt<T>(EventList<T>? list, int count, int index, T item)
        where T : class
        => list is { IsDestroyed: false, IsDestroyQueued: false } &&
            list.Count == count && (uint)index < (uint)count &&
            ReferenceEquals(list[index], item);

    private static int UniqueIndex<T>(EventList<T> list, T item) where T : class
    {
        int found = -1;
        for (int index = 0; index < list.Count; index++)
            if (ReferenceEquals(list[index], item))
            {
                if (found >= 0) return -1;
                found = index;
            }
        return found;
    }
}

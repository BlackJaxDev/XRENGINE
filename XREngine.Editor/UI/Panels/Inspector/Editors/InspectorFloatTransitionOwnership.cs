using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Retains the exact state-machine path of one transition.</summary>
internal sealed class InspectorFloatTransitionOwnership
{
    private readonly AnimStateTransition _transition;
    private readonly AnimState? _state;
    private readonly AnimLayer? _layer;
    private readonly AnimStateMachine? _machine;
    private readonly EventList<AnimStateTransition>? _transitions;
    private readonly EventList<AnimState>? _states;
    private readonly EventList<AnimLayer>? _layers;
    private readonly int _transitionIndex;
    private readonly int _stateIndex;
    private readonly int _layerIndex;
    private readonly int _transitionCount;
    private readonly int _stateCount;
    private readonly int _layerCount;
    private readonly Guid _machineIdentity;
    private readonly Guid _documentIdentity;

    private InspectorFloatTransitionOwnership(AnimStateTransition transition,
        AnimState? state = null, AnimLayer? layer = null,
        AnimStateMachine? machine = null, int transitionIndex = -1,
        int stateIndex = -1, int layerIndex = -1)
    {
        _transition = transition;
        _state = state;
        _layer = layer;
        _machine = machine;
        _transitions = state?.Transitions;
        _states = layer?.States;
        _layers = machine?.Layers;
        _transitionIndex = transitionIndex;
        _stateIndex = stateIndex;
        _layerIndex = layerIndex;
        _transitionCount = _transitions?.Count ?? 0;
        _stateCount = _states?.Count ?? 0;
        _layerCount = _layers?.Count ?? 0;
        _machineIdentity = machine?.ID ?? Guid.Empty;
        Document = machine?.SourceAsset;
        _documentIdentity = Document?.ID ?? Guid.Empty;
    }

    public XRAsset? Document { get; }

    public static bool TryCapture(AnimStateTransition transition,
        out InspectorFloatTransitionOwnership? ownership, out string reason)
    {
        ownership = null;
        reason = "The transition ownership path is incomplete.";
        if (transition.Owner is null)
        {
            ownership = new(transition);
            reason = string.Empty;
            return true;
        }
        if (transition.Owner is not AnimState state ||
            state.OwningLayer is not { } layer ||
            layer.OwningStateMachine is not { } machine ||
            machine.IsDestroyed || machine.IsDestroyQueued)
            return false;
        if (state.Transitions is not { } transitions ||
            layer.States is not { } states ||
            machine.Layers is not { } layers ||
            transitions.IsDestroyed || transitions.IsDestroyQueued ||
            states.IsDestroyed || states.IsDestroyQueued ||
            layers.IsDestroyed || layers.IsDestroyQueued)
            return false;
        int transitionIndex = UniqueIndex(transitions, transition);
        int stateIndex = UniqueIndex(states, state);
        int layerIndex = UniqueIndex(layers, layer);
        if (transitionIndex < 0 || stateIndex < 0 || layerIndex < 0)
        {
            reason = "The transition is absent or repeated in its state-machine path.";
            return false;
        }
        ownership = new(transition, state, layer, machine,
            transitionIndex, stateIndex, layerIndex);
        if (!ownership.IsCurrent())
        {
            ownership = null;
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public bool IsCurrent()
        => IsCurrentFast() && (_state is null ||
            UniqueIndex(_transitions!, _transition) == _transitionIndex &&
            UniqueIndex(_states!, _state) == _stateIndex &&
            UniqueIndex(_layers!, _layer!) == _layerIndex);

    public bool IsCurrentFast()
    {
        if (_state is null)
            return _transition.Owner is null;
        return _machine is { IsDestroyed: false, IsDestroyQueued: false } machine &&
            Document is { IsDestroyed: false, IsDestroyQueued: false } document &&
            machine.ID == _machineIdentity && document.ID == _documentIdentity &&
            ReferenceEquals(machine.SourceAsset, document) &&
            ReferenceEquals(_transition.Owner, _state) &&
            ReferenceEquals(_state.Transitions, _transitions) &&
            _transitions?.Count == _transitionCount &&
            HasAt(_transitions, _transitionIndex, _transition) &&
            ReferenceEquals(_state.OwningLayer, _layer) &&
            ReferenceEquals(_layer?.States, _states) &&
            _states?.Count == _stateCount &&
            HasAt(_states, _stateIndex, _state) &&
            ReferenceEquals(_layer?.OwningStateMachine, machine) &&
            ReferenceEquals(machine.Layers, _layers) &&
            _layers?.Count == _layerCount &&
            HasAt(_layers, _layerIndex, _layer!);
    }

    private static bool HasAt<T>(EventList<T>? list, int index, T item)
        where T : class
        => list is { IsDestroyed: false, IsDestroyQueued: false } &&
            (uint)index < (uint)list.Count &&
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

using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Scene;

namespace XREngine.Cutscenes;

/// <summary>
/// Keeps the sibling animation components of a node from writing while a cutscene clip drives the node.
/// The lease records the earlier suspension of each component and puts it back at release. Playback time
/// and the play and pause state of the components do not change. An acquire allocates only when the lease
/// sees a new node or a new last component.
/// </summary>
public sealed class CutsceneAnimationLease
{
    private readonly List<AnimStateMachineComponent> _machines = new(2);
    private readonly List<AnimationClipComponent> _players = new(2);
    private readonly List<bool> _machineWasSuspended = new(2);
    private readonly List<bool> _playerWasSuspended = new(2);
    private SceneNode? _scannedNode;

    /// <summary>True from <see cref="Acquire"/> until <see cref="Release"/>.</summary>
    public bool IsHeld { get; private set; }

    /// <summary>Suspends the animation components of a node. A call on a held lease does nothing.</summary>
    /// <param name="node">The node that the cutscene drives.</param>
    public void Acquire(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (IsHeld)
            return;

        Scan(node);
        IsHeld = true;

        for (int i = 0; i < _machines.Count; i++)
        {
            _machineWasSuspended[i] = _machines[i].EvaluationSuspended;
            _machines[i].SetEvaluationSuspended(true);
        }
        for (int i = 0; i < _players.Count; i++)
        {
            _playerWasSuspended[i] = _players[i].EvaluationSuspended;
            _players[i].SetEvaluationSuspended(true);
        }
    }

    /// <summary>Gives each component its earlier suspension back. A call on a free lease does nothing.</summary>
    public void Release()
    {
        if (!IsHeld)
            return;
        IsHeld = false;

        for (int i = 0; i < _machines.Count; i++)
        {
            if (!_machines[i].IsDestroyed)
                _machines[i].SetEvaluationSuspended(_machineWasSuspended[i]);
        }
        for (int i = 0; i < _players.Count; i++)
        {
            if (!_players[i].IsDestroyed)
                _players[i].SetEvaluationSuspended(_playerWasSuspended[i]);
        }
    }

    private void Scan(SceneNode node)
    {
        // The node exposes no component list without an enumerator. The last component of each type shows an addition without a walk.
        if (ReferenceEquals(_scannedNode, node) && IsKnownLast(node))
            return;

        _scannedNode = node;
        _machines.Clear();
        _players.Clear();
        _machines.AddRange(node.GetComponents<AnimStateMachineComponent>());
        _players.AddRange(node.GetComponents<AnimationClipComponent>());
        Resize(_machineWasSuspended, _machines.Count);
        Resize(_playerWasSuspended, _players.Count);
    }

    private bool IsKnownLast(SceneNode node)
    {
        AnimStateMachineComponent? machine = node.GetLastComponent<AnimStateMachineComponent>();
        AnimationClipComponent? player = node.GetLastComponent<AnimationClipComponent>();
        return ReferenceEquals(machine, _machines.Count > 0 ? _machines[^1] : null)
            && ReferenceEquals(player, _players.Count > 0 ? _players[^1] : null);
    }

    private static void Resize(List<bool> list, int count)
    {
        list.Clear();
        for (int i = 0; i < count; i++)
            list.Add(false);
    }
}

namespace XREngine.Components.Animation;

/// <summary>
/// Preallocated frame of avatar-evaluated state-machine Body/root leaves.
/// </summary>
internal sealed class HumanoidStateMachineRootMotionFrame(int capacity)
{
    private readonly HumanoidStateMachineRootMotionLeafState?[] _leaves =
        capacity > 0 ? new HumanoidStateMachineRootMotionLeafState[capacity] : [];

    public int Count { get; private set; }
    public ReadOnlySpan<HumanoidStateMachineRootMotionLeafState?> Leaves
        => _leaves.AsSpan(0, Count);

    public void Clear()
        => Count = 0;

    public bool TryAdd(HumanoidStateMachineRootMotionLeafState leaf)
    {
        if (Count >= _leaves.Length)
            return false;

        _leaves[Count++] = leaf;
        return true;
    }

    /// <summary>
    /// Starts rollback scopes for each persistent leaf before feet projection
    /// can mutate its cached root poses.
    /// </summary>
    internal void BeginFeetProjectionTransaction()
    {
        for (int i = 0; i < Count; i++)
            _leaves[i]?.BeginFeetProjectionTransaction();
    }

    /// <summary>
    /// Commits or restores all leaf feet-projection cache changes as one frame.
    /// </summary>
    internal void ResolveFeetProjectionTransaction(bool accepted)
    {
        for (int i = 0; i < Count; i++)
            _leaves[i]?.ResolveFeetProjectionTransaction(accepted);
    }
}

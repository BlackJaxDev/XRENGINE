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
}

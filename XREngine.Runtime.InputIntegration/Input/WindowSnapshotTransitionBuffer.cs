namespace XREngine.Runtime.InputIntegration;

/// <summary>
/// Retains ordered snapshot edges until the update-side mapped input tick. Overflow abandons
/// the batch; its owner must neutralize held channels instead of losing a release.
/// </summary>
internal sealed class WindowSnapshotTransitionBuffer<T> where T : struct
{
    // Covers the browser's 256 accepted transitions plus releases for every held key/button.
    private readonly T[] _transitions = new T[512];
    private int _readIndex;
    private int _count;

    public bool TryAppend(ReadOnlySpan<T> transitions)
    {
        if (transitions.Length > _transitions.Length - _count)
            return false;
        transitions.CopyTo(_transitions.AsSpan(_count));
        _count += transitions.Length;
        return true;
    }

    public bool TryDequeue(out T transition)
    {
        if (_readIndex == _count)
        {
            transition = default;
            return false;
        }
        transition = _transitions[_readIndex++];
        if (_readIndex == _count)
            Clear();
        return true;
    }

    public void Clear()
        => _readIndex = _count = 0;
}

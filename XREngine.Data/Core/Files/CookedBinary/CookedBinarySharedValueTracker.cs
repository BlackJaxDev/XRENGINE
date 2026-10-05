namespace XREngine.Core.Files;

/// <summary>
/// Numbers the shared values of one serialization pass in the order their definitions begin.
/// The size, write and schema passes each use their own tracker and walk the graph in the same
/// order, so they make the same decisions and assign the same identities.
/// </summary>
internal sealed class CookedBinarySharedValueTracker
{
    private readonly Dictionary<object, int> _completed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _inProgress = new(ReferenceEqualityComparer.Instance);
    private int _nextIdentity;

    /// <summary>Returns the identity of a value whose definition has been written in full.</summary>
    public bool TryGetCompleted(object value, out int identity)
        => _completed.TryGetValue(value, out identity);

    /// <summary>
    /// Starts the definition of a value and assigns its identity. Returns false while the
    /// value's own definition is still being written: that occurrence is a cycle, which the
    /// caller writes by value.
    /// </summary>
    public bool TryBegin(object value, out int identity)
    {
        if (!_inProgress.Add(value))
        {
            identity = -1;
            return false;
        }

        identity = _nextIdentity++;
        return true;
    }

    /// <summary>Marks a definition written in full; later occurrences become references to it.</summary>
    public void Complete(object value, int identity)
    {
        _inProgress.Remove(value);
        _completed.Add(value, identity);
    }
}

namespace XREngine.Core.Files;

/// <summary>
/// Holds the shared values one deserialization has read, by identity. A definition is
/// registered once its value has been read in full, which is when the writer allows
/// references to it, so a reference to an unknown or unfinished identity is malformed data.
/// </summary>
internal sealed class CookedBinarySharedValueTable(CookedBinarySerializationCallbacks callbacks)
{
    private static readonly object Pending = new();

    private readonly List<object?> _values = [];

    /// <summary>
    /// The callbacks of the deserialization, used when a definition is met where its
    /// occurrence is skipped: later references still need the value read with them.
    /// </summary>
    public CookedBinarySerializationCallbacks Callbacks { get; } = callbacks;

    /// <summary>Reserves the identity of a definition about to be read.</summary>
    public void Begin(int identity)
    {
        if (identity != _values.Count)
            throw new InvalidDataException($"Cooked shared value {identity} is defined out of order; expected {_values.Count}.");

        _values.Add(Pending);
    }

    /// <summary>Registers the value of a definition read in full.</summary>
    public void Complete(int identity, object? value)
        => _values[identity] = value;

    /// <summary>Returns the value of an identity whose definition has been read in full.</summary>
    public object? Resolve(int identity)
    {
        if ((uint)identity >= (uint)_values.Count)
            throw new InvalidDataException($"Cooked shared value {identity} is referenced before its definition.");

        object? value = _values[identity];
        if (ReferenceEquals(value, Pending))
            throw new InvalidDataException($"Cooked shared value {identity} is referenced inside its own definition.");

        return value;
    }
}

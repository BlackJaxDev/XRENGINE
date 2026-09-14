using System.Collections;

namespace XREngine.Editor;

/// <summary>
/// Reusable values for one member and an immutable inspector selection. A single target
/// needs no array; multi-selection storage is allocated once with the selection.
/// </summary>
internal sealed class InspectorValueBuffer(int count) : IReadOnlyList<object?>
{
    private object? _singleValue;
    private readonly object?[]? _multipleValues = count > 1 ? new object?[count] : null;

    public int Count { get; } = count;

    public object? this[int index]
    {
        get => _multipleValues is not null ? _multipleValues[index] : index == 0 ? _singleValue : throw new ArgumentOutOfRangeException(nameof(index));
        set
        {
            if (_multipleValues is not null)
                _multipleValues[index] = value;
            else if (index == 0)
                _singleValue = value;
            else
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public void Clear()
    {
        _singleValue = null;
        if (_multipleValues is not null)
            Array.Clear(_multipleValues);
    }

    public IEnumerator<object?> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

namespace XREngine.Editor.UI;

/// <summary>Records the actual key objects and values of one dictionary.</summary>
internal sealed class EditorDictionarySnapshot
{
    private readonly Dictionary<string, int>? _stringPositions;
    private readonly Dictionary<int, int>? _intPositions;

    public EditorDictionarySnapshot(object[] keys, object?[] values, Type keyType)
    {
        if (keys.Length != values.Length)
            throw new ArgumentException("Dictionary keys and values must have the same count.");
        Keys = keys;
        Values = values;
        if (keyType == typeof(string))
        {
            _stringPositions = new(keys.Length, StringComparer.Ordinal);
            for (int index = 0; index < keys.Length; index++)
                _stringPositions.Add((string)keys[index], index);
        }
        else if (keyType == typeof(int))
        {
            _intPositions = new(keys.Length);
            for (int index = 0; index < keys.Length; index++)
                _intPositions.Add((int)keys[index], index);
        }
        else throw new ArgumentException("Only intrinsic dictionary keys are supported.", nameof(keyType));
    }

    public object[] Keys { get; }
    public object?[] Values { get; }

    public int IndexOf(object key)
    {
        if (key is string text && _stringPositions?.TryGetValue(text, out int textIndex) == true)
            return textIndex;
        if (key is int number && _intPositions?.TryGetValue(number, out int numberIndex) == true)
            return numberIndex;
        return -1;
    }
}

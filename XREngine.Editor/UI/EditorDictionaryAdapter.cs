using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace XREngine.Editor.UI;

/// <summary>Reads and writes only exact BCL dictionaries with known key comparers.</summary>
internal sealed class EditorDictionaryAdapter
{
    private readonly Type _keyType;
    private readonly Type _valueType;

    private EditorDictionaryAdapter(Type keyType, Type valueType)
    {
        _keyType = keyType;
        _valueType = valueType;
    }

    public Type KeyType => _keyType;
    public Type ValueType => _valueType;

    public static EditorDictionaryAdapter? For(object? value)
    {
        if (value is not IDictionary dictionary) return null;
        Type type = value.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            return null;
        Type[] args = type.GetGenericArguments();
        if (args[0] != typeof(string) && args[0] != typeof(int) ||
            !EditorListMutation.IsComparableElementType(args[1]))
            return null;
        object comparer = type.GetProperty("Comparer")!.GetValue(value)!;
        object canonical = typeof(EqualityComparer<>).MakeGenericType(args[0])
            .GetProperty("Default", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        if (!ReferenceEquals(comparer, canonical) &&
            !(args[0] == typeof(string) && ReferenceEquals(comparer, StringComparer.Ordinal)))
            return null;
        return dictionary.IsReadOnly ? null : new(args[0], args[1]);
    }

    public bool KeyEquals(object left, object right)
        => _keyType == typeof(int) ? (int)left == (int)right :
            string.Equals((string)left, (string)right, StringComparison.Ordinal);

    public bool SameKey(object actual, object expected)
        => KeyEquals(actual, expected) &&
            (_keyType == typeof(int) || ReferenceEquals(actual, expected));

    public EditorDictionarySnapshot Snapshot(object dictionary)
    {
        IDictionary source = (IDictionary)dictionary;
        object[] keys = new object[source.Count];
        object?[] values = new object?[source.Count];
        int index = 0;
        foreach (DictionaryEntry entry in source)
        {
            if (index >= keys.Length)
                throw new InvalidOperationException("The dictionary changed during inspection.");
            keys[index] = entry.Key;
            values[index] = entry.Value;
            index++;
        }
        if (index != keys.Length)
            throw new InvalidOperationException("The dictionary changed during inspection.");
        return new(keys, values, _keyType);
    }

    public bool Matches(object dictionary, EditorDictionarySnapshot expected)
    {
        IDictionary source = (IDictionary)dictionary;
        if (source.Count != expected.Keys.Length) return false;
        foreach (DictionaryEntry entry in source)
        {
            int index = IndexOf(expected, entry.Key);
            if (index < 0 || !SameKey(entry.Key, expected.Keys[index]) ||
                !EditorListMutation.SameValue(entry.Value, expected.Values[index]))
                return false;
        }
        return true;
    }

    public int IndexOf(EditorDictionarySnapshot snapshot, object key)
        => snapshot.IndexOf(key);

    public void Set(object dictionary, object key, object? value)
        => ((IDictionary)dictionary)[key] = value;

    public void Add(object dictionary, object key, object? value)
        => ((IDictionary)dictionary).Add(key, value);

    public void Remove(object dictionary, object key)
        => ((IDictionary)dictionary).Remove(key);

    public void ReserveCapacity(object dictionary, int count)
    {
        MethodInfo method = dictionary.GetType().GetMethod("EnsureCapacity", [typeof(int)])!;
        method.Invoke(dictionary, [count]);
    }
}

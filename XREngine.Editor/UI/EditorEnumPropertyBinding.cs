using System.Collections.Concurrent;
using System.Reflection;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>Supplies stable enum choices and retained property binding to a virtual combo.</summary>
internal sealed class EditorEnumPropertyBinding : IUIValueBinding<ulong?>, IUIComboOptionSource
{
    private static readonly ConcurrentDictionary<Type, (Array Values, string[] Names)> Metadata = new();
    private readonly EditorPropertyAccess _access;
    private readonly Array _values;
    private readonly string[] _names;
    private Action<ulong?>? _changed;

    public EditorEnumPropertyBinding(PropertyInfo property, object?[] targets)
    {
        _access = new(property, targets);
        (_values, _names) = Metadata.GetOrAdd(property.PropertyType,
            static type => (Enum.GetValues(type), Enum.GetNames(type)));
    }

    public int Count => _names.Length;
    public ulong Revision => 1;
    public UIComboOption GetOption(int index) => new((ulong)index + 1, _names[index]);
    public int IndexOfId(ulong id) => id > 0 && id <= (ulong)Count ? (int)id - 1 : -1;

    public ulong? Value
    {
        get
        {
            if (!_access.TryGetCommonValue(out object? value)) return null;
            for (int index = 0; index < _values.Length; index++)
                if (Equals(value, _values.GetValue(index))) return (ulong)index + 1;
            return null;
        }
    }

    public event Action<ulong?>? ValueChanged
    {
        add
        {
            if (_changed is null) _access.Changed += Notify;
            _changed += value;
        }
        remove
        {
            _changed -= value;
            if (_changed is null) _access.Changed -= Notify;
        }
    }

    public void SetValue(ulong? value)
    {
        int index = value is { } id ? IndexOfId(id) : -1;
        if (index >= 0) _access.SetValue(_values.GetValue(index));
    }

    private void Notify() => _changed?.Invoke(Value);
}

using System.Collections.Concurrent;
using System.Reflection;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>Supplies stable enum choices and retained property binding to a virtual combo.</summary>
internal sealed class EditorEnumPropertyBinding : IUIValueBinding<ulong?>, IUIComboOptionSource,
    IUIComboPrefixSearch, IUIComboEnabledNavigation, IDisposable
{
    private static readonly ConcurrentDictionary<Type, (Array Values, string[] Names)> Metadata = new();
    private readonly EditorPropertyAccess _access;
    private readonly Array _values;
    private readonly string[] _names;
    private readonly EditorComboPrefixIndex _prefixIndex;
    private Action<ulong?>? _changed;
    private bool _retired;
    private readonly Action _validateWrite;
    internal bool IsRetired => _retired;

    public EditorEnumPropertyBinding(PropertyInfo property, object?[] targets)
    {
        _access = new(property, targets);
        _validateWrite = ValidateWrite;
        (_values, _names) = Metadata.GetOrAdd(property.PropertyType,
            static type => (Enum.GetValues(type), Enum.GetNames(type)));
        _prefixIndex = new EditorComboPrefixIndex(_names);
    }

    public int Count => _names.Length;
    public ulong Revision => 1;
    public UIComboOption GetOption(int index) => new((ulong)index + 1, _names[index]);
    public int IndexOfId(ulong id) => id > 0 && id <= (ulong)Count ? (int)id - 1 : -1;
    public int FindPrefix(string prefix, int startIndex) => _prefixIndex.Find(prefix, startIndex);
    public int FindNextEnabled(int startIndex, int direction)
    {
        if (Count == 0 || direction is not (1 or -1) || startIndex < -1 || startIndex >= Count)
            return -1;
        return direction > 0
            ? startIndex + 1 == Count ? 0 : startIndex + 1
            : startIndex <= 0 ? Count - 1 : startIndex - 1;
    }

    public ulong? Value
    {
        get
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            bool common = _access.TryGetCommonValue(out object? value);
            ObjectDisposedException.ThrowIf(_retired, this);
            if (!common) return null;
            for (int index = 0; index < _values.Length; index++)
                if (Equals(value, _values.GetValue(index))) return (ulong)index + 1;
            return null;
        }
    }

    public event Action<ulong?>? ValueChanged
    {
        add
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            if (_changed is null) _access.Changed += Notify;
            if (_retired)
            {
                _access.Changed -= Notify;
                throw new ObjectDisposedException(nameof(EditorEnumPropertyBinding));
            }
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
        ObjectDisposedException.ThrowIf(_retired, this);
        int index = value is { } id ? IndexOfId(id) : -1;
        if (index >= 0) _access.SetValue(_values.GetValue(index), _validateWrite);
    }

    private void Notify()
    {
        if (_retired) return;
        ulong? value;
        try { value = Value; }
        catch (ObjectDisposedException) when (_retired) { return; }
        if (!_retired) _changed?.Invoke(value);
    }

    private void ValidateWrite() => ObjectDisposedException.ThrowIf(_retired, this);

    public void Dispose()
    {
        if (_retired) return;
        _retired = true;
        _changed = null;
        _access.Changed -= Notify;
    }
}

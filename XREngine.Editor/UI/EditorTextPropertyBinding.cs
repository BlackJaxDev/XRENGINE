using System.Globalization;
using System.Reflection;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>Connects retained text to cached editor property access.</summary>
internal sealed class EditorTextPropertyBinding : IUIValueBinding<string>
{
    private readonly EditorPropertyAccess _access;
    private readonly PropertyInfo _property;
    private readonly Type _valueType;
    private Action<string>? _changed;

    public EditorTextPropertyBinding(PropertyInfo property, object?[] targets)
    {
        _property = property;
        _access = new(property, targets);
        _valueType = property.PropertyType;
    }

    public PropertyInfo Property => _property;

    public string Value => _access.TryGetCommonValue(out object? value)
        ? Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty : string.Empty;

    public event Action<string>? ValueChanged
    {
        add
        {
            if (_changed is null)
                _access.Changed += Notify;
            _changed += value;
        }
        remove
        {
            _changed -= value;
            if (_changed is null)
                _access.Changed -= Notify;
        }
    }

    public void SetValue(string value)
    {
        Type targetType = Nullable.GetUnderlyingType(_valueType) ?? _valueType;
        object? selected = targetType == typeof(string) ? value :
            string.IsNullOrEmpty(value) && targetType != _valueType ? null :
            targetType.IsEnum ? Enum.Parse(targetType, value, ignoreCase: true) :
            Convert.ChangeType(value, targetType, CultureInfo.CurrentCulture);
        _access.SetValue(selected);
    }

    private void Notify() => _changed?.Invoke(Value);
}

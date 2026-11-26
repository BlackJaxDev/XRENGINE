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
    private readonly bool _nullMeansMixed;
    private Action<string>? _changed;

    /// <summary>
    /// The text that a field shows when the edited objects have different values. Writing this text back
    /// leaves the values unchanged.
    /// </summary>
    public const string MixedValueText = "\u2014";

    public EditorTextPropertyBinding(PropertyInfo property, object?[] targets)
    {
        _property = property;
        _access = new(property, targets);
        _valueType = property.PropertyType;
        // A field-list transformer, such as the X, Y, and Z fields of a vector, shows null when the edited
        // objects have different vectors.
        _nullMeansMixed = targets.Length > 0 &&
            Array.TrueForAll(targets, static target => target is InspectorPropertyEditors.DataTransformerBase);
    }

    public PropertyInfo Property => _property;

    public string Value => !_access.TryGetCommonValue(out object? value) || value is null && _nullMeansMixed
        ? MixedValueText
        : Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;

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
        if (value == MixedValueText)
            return;
        Type targetType = Nullable.GetUnderlyingType(_valueType) ?? _valueType;
        object? selected = targetType == typeof(string) ? value :
            string.IsNullOrEmpty(value) && targetType != _valueType ? null :
            targetType.IsEnum ? Enum.Parse(targetType, value, ignoreCase: true) :
            Convert.ChangeType(value, targetType, CultureInfo.CurrentCulture);
        _access.SetValue(selected);
    }

    private void Notify() => _changed?.Invoke(Value);
}

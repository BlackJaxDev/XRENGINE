using System.Numerics;
using System.ComponentModel;
using XREngine.Components;
using XREngine.Data.Colors;
using YamlDotNet.Serialization;

namespace XREngine.Components;

[XRComponentEditor("XREngine.Editor.ComponentEditors.CustomUIComponentEditor")]
public sealed class CustomUIComponent : XRComponent
{
    private readonly List<CustomUIField> _fields = [];

    [Browsable(false)]
    [YamlIgnore]
    public IReadOnlyList<CustomUIField> Fields => _fields;

    public void ClearFields()
        => _fields.Clear();

    public CustomUIFloatField AddFloatField(
        string label,
        Func<float> getter,
        Action<float> setter,
        float min,
        float max,
        float step = 0.1f,
        string format = "%.3f",
        string? helpText = null)
    {
        var field = new CustomUIFloatField(label, getter, setter, min, max, step, format, helpText);
        _fields.Add(field);
        return field;
    }

    public CustomUIBoolField AddBoolField(
        string label,
        Func<bool> getter,
        Action<bool> setter,
        string? helpText = null)
    {
        var field = new CustomUIBoolField(label, getter, setter, helpText);
        _fields.Add(field);
        return field;
    }

    public CustomUIVector3Field AddVector3Field(
        string label,
        Func<Vector3> getter,
        Action<Vector3> setter,
        float step = 0.1f,
        string format = "%.3f",
        string? helpText = null)
    {
        var field = new CustomUIVector3Field(label, getter, setter, step, format, helpText);
        _fields.Add(field);
        return field;
    }

    public CustomUIColorField AddColorField(
        string label,
        Func<ColorF4> getter,
        Action<ColorF4> setter,
        bool alpha = true,
        string? helpText = null)
    {
        var field = new CustomUIColorField(label, getter, setter, alpha, helpText);
        _fields.Add(field);
        return field;
    }

    public CustomUITextField AddTextField(
        string label,
        Func<string> getter,
        string? helpText = null)
    {
        var field = new CustomUITextField(label, getter, helpText);
        _fields.Add(field);
        return field;
    }

    public CustomUIButtonField AddButtonField(
        string label,
        Action action,
        string? helpText = null)
    {
        var field = new CustomUIButtonField(label, action, helpText);
        _fields.Add(field);
        return field;
    }
}

public abstract class CustomUIField(string label, string? helpText = null)
{
    public string Label { get; } = label;
    public string? HelpText { get; } = helpText;
}

public sealed class CustomUIFloatField(
    string label,
    Func<float> getter,
    Action<float> setter,
    float min,
    float max,
    float step,
    string format,
    string? helpText = null)
    : CustomUIField(label, helpText)
{
    public float Min { get; } = min;
    public float Max { get; } = max;
    public float Step { get; } = step;
    public string Format { get; } = format;

    public float GetValue()
        => getter();

    public void SetValue(float value)
        => setter(value);
}

public sealed class CustomUIBoolField(
    string label,
    Func<bool> getter,
    Action<bool> setter,
    string? helpText = null)
    : CustomUIField(label, helpText)
{
    public bool GetValue()
        => getter();

    public void SetValue(bool value)
        => setter(value);
}

public sealed class CustomUIVector3Field(
    string label,
    Func<Vector3> getter,
    Action<Vector3> setter,
    float step,
    string format,
    string? helpText = null)
    : CustomUIField(label, helpText)
{
    public float Step { get; } = step;
    public string Format { get; } = format;

    public Vector3 GetValue()
        => getter();

    public void SetValue(Vector3 value)
        => setter(value);
}

public sealed class CustomUIColorField(
    string label,
    Func<ColorF4> getter,
    Action<ColorF4> setter,
    bool alpha,
    string? helpText = null)
    : CustomUIField(label, helpText)
{
    public bool Alpha { get; } = alpha;

    public ColorF4 GetValue()
        => getter();

    public void SetValue(ColorF4 value)
        => setter(value);
}

public sealed class CustomUITextField(
    string label,
    Func<string> getter,
    string? helpText = null)
    : CustomUIField(label, helpText)
{
    public string GetValue()
        => getter();
}

public sealed class CustomUIButtonField(
    string label,
    Action action,
    string? helpText = null)
    : CustomUIField(label, helpText)
{
    public void Invoke()
        => action();
}
using System.Drawing;
using System.Reflection;
using XREngine.Data.Colors;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>Maps native color controls to linear engine colors or byte sRGB properties.</summary>
internal sealed class EditorColorPropertyBinding : IUIValueBinding<ColorF4>
{
    private readonly EditorPropertyAccess _access;
    private readonly Type _type;
    private Action<ColorF4>? _changed;

    public EditorColorPropertyBinding(PropertyInfo property, object?[] targets)
    {
        _access = new(property, targets);
        _type = property.PropertyType;
    }

    public ColorF4 Value => _access.TryGetCommonValue(out object? value) ? value switch
    {
        ColorF4 color => color,
        ColorF3 color => new(color.R, color.G, color.B, 1),
        Color color => UIColorSelectorComponent.SrgbToLinear(new(color.R / 255f, color.G / 255f,
            color.B / 255f, color.A / 255f)),
        _ => ColorF4.Transparent,
    } : ColorF4.Transparent;

    public event Action<ColorF4>? ValueChanged
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

    public void SetValue(ColorF4 value)
    {
        if (_type == typeof(ColorF4)) _access.SetValue(value);
        else if (_type == typeof(ColorF3)) _access.SetValue(new ColorF3(value.R, value.G, value.B));
        else
        {
            ColorF4 srgb = UIColorSelectorComponent.LinearToSrgb(value);
            _access.SetValue(Color.FromArgb(ToByte(srgb.A), ToByte(srgb.R), ToByte(srgb.G), ToByte(srgb.B)));
        }
    }

    private static int ToByte(float value) => Math.Clamp((int)MathF.Round(value * 255), 0, 255);
    private void Notify() => _changed?.Invoke(Value);
}

using System.Numerics;
using XREngine.Input.Devices;
using YamlDotNet.Serialization;

namespace XREngine.Components;

/// <summary>A fixed-center radial stick mapping local UI coordinates to existing gamepad axes.</summary>
public sealed class UIVirtualStickComponent : UIVirtualInputComponent
{
    private EGamePadAxis _horizontalAxis = EGamePadAxis.LeftThumbstickX;
    private EGamePadAxis _verticalAxis = EGamePadAxis.LeftThumbstickY;
    private float _deadZone = 0.12f;
    private Vector2 _value;

    public EGamePadAxis HorizontalAxis
    {
        get => _horizontalAxis;
        set
        {
            if ((uint)value >= 6)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _horizontalAxis, value))
                CancelPointer();
        }
    }

    public EGamePadAxis VerticalAxis
    {
        get => _verticalAxis;
        set
        {
            if ((uint)value >= 6)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _verticalAxis, value))
                CancelPointer();
        }
    }

    public float DeadZone
    {
        get => _deadZone;
        set
        {
            if (!float.IsFinite(value) || value < 0 || value >= 1)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _deadZone, value))
                CancelPointer();
        }
    }

    /// <summary>Current radial value, positive Y upward. Polling does not allocate.</summary>
    [YamlIgnore]
    public Vector2 Value => _value;

    internal override void UpdatePointer(Vector2 localPosition)
    {
        Vector2 size = BoundableTransform.ActualSize;
        float radius = MathF.Min(size.X, size.Y) * 0.5f;
        if (!float.IsFinite(radius) || radius <= 0)
        {
            _value = Vector2.Zero;
            return;
        }
        Vector2 delta = (localPosition - size * 0.5f) / radius;
        float length = delta.Length();
        _value = !float.IsFinite(length) || length <= _deadZone ? Vector2.Zero
            : delta / length * ((MathF.Min(length, 1) - _deadZone) / (1 - _deadZone));
    }

    internal override void WriteAxes(Span<float> axes)
    {
        WriteAxis(axes, _horizontalAxis, _value.X);
        WriteAxis(axes, _verticalAxis, _value.Y);
    }

    private static void WriteAxis(Span<float> axes, EGamePadAxis axis, float value)
    {
        if (axis is EGamePadAxis.LeftTrigger or EGamePadAxis.RightTrigger)
            value = MathF.Max(0, value);
        if (MathF.Abs(value) > MathF.Abs(axes[(int)axis]))
            axes[(int)axis] = value;
    }

    protected override void ResetValue() => _value = Vector2.Zero;
}

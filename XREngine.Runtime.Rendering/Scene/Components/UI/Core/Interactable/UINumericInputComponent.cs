using System.Globalization;
using System.Numerics;
using System.Reflection;
using XREngine.Input;
using XREngine.Input.Devices;

namespace XREngine.Rendering.UI;

/// <summary>Applies one numeric policy to text drafts, commits, and step changes.</summary>
public abstract class UINumericInputComponent<T> : UITextInputComponent where T : struct, INumber<T>
{
    private T? _minimum;
    private T? _maximum;
    private T _step = T.One;
    private CultureInfo? _culture;
    private bool _scrubbing;
    private string _scrubStartText = string.Empty;
    private T _scrubStartValue = T.Zero;
    private float _scrubStartX;
    private float _scrubPixelsPerStep = 8;
    private bool _enableAltScrub = true;
    private static readonly bool IsIntegral = typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte) ||
        typeof(T) == typeof(short) || typeof(T) == typeof(ushort) ||
        typeof(T) == typeof(int) || typeof(T) == typeof(uint) ||
        typeof(T) == typeof(long) || typeof(T) == typeof(ulong);

    protected UINumericInputComponent()
    {
        SingleLineMode = true;
        CommitOnSubmit = true;
        AllowInvalidDraft = true;
        NeedsMouseMove = true;
    }

    /// <summary>Uses the current culture when no culture is set.</summary>
    public CultureInfo? Culture
    {
        get => _culture;
        set => SetField(ref _culture, value);
    }

    /// <summary>Alt with the primary pointer scrubs values. A normal pointer edits text.</summary>
    public bool EnableAltScrub
    {
        get => _enableAltScrub;
        set
        {
            if (_enableAltScrub == value)
                return;
            if (!value)
                EndScrub(false);
            SetField(ref _enableAltScrub, value);
        }
    }

    public float ScrubPixelsPerStep
    {
        get => _scrubPixelsPerStep;
        set
        {
            if (!float.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _scrubPixelsPerStep, value);
        }
    }

    public bool IsScrubbing => _scrubbing;

    public T? Minimum
    {
        get => _minimum;
        set
        {
            if (value is { } minimum && (!T.IsFinite(minimum) ||
                _maximum is { } maximum && minimum > maximum))
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _minimum, value);
        }
    }

    public T? Maximum
    {
        get => _maximum;
        set
        {
            if (value is { } maximum && (!T.IsFinite(maximum) ||
                _minimum is { } minimum && maximum < minimum))
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _maximum, value);
        }
    }

    public T Step
    {
        get => _step;
        set
        {
            if (!T.IsFinite(value) || value <= T.Zero)
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _step, value);
        }
    }

    public override bool PostValidateInput(string input) => TryParseInRange(input, out _);

    public bool TryStep(int direction)
    {
        if (!CanInteract || IsReadOnly || IsComposing || direction == 0 ||
            !TryParseInRange(Text, out T current))
            return false;
        if (!TryAddSteps(current, Math.Sign(direction), out T value) || value == current)
            return false;
        Text = value.ToString(null, Culture ?? CultureInfo.CurrentCulture);
        UserSubmit();
        return true;
    }

    private bool TryAddSteps(T current, int steps, out T value)
    {
        value = current;
        if (steps == 0)
            return true;
        if (IsIntegral)
        {
            // Int128 holds every supported integer value and one million steps.
            Int128 exact = checked(Int128.CreateChecked(current) +
                Int128.CreateChecked(_step) * steps);
            if (_minimum is { } lower)
            {
                Int128 lowerBound = Int128.CreateChecked(lower);
                if (exact < lowerBound)
                    exact = lowerBound;
            }
            if (_maximum is { } upper)
            {
                Int128 upperBound = Int128.CreateChecked(upper);
                if (exact > upperBound)
                    exact = upperBound;
            }
            try { value = T.CreateChecked(exact); }
            catch (OverflowException) { return false; }
            return true;
        }
        try
        {
            T amount = checked(_step * T.CreateChecked(Math.Abs(steps)));
            value = checked(steps > 0 ? current + amount : current - amount);
        }
        catch (OverflowException)
        {
            if (steps > 0 && _maximum is { } upper)
                value = upper;
            else if (steps < 0 && _minimum is { } lower)
                value = lower;
            else
                return false;
        }
        if (!T.IsFinite(value))
        {
            if (steps > 0 && _maximum is { } upper)
                value = upper;
            else if (steps < 0 && _minimum is { } lower)
                value = lower;
            else
                return false;
        }
        if (_minimum is { } minimum && value < minimum)
            value = minimum;
        if (_maximum is { } maximum && value > maximum)
            value = maximum;
        return true;
    }

    public override void PointerPressed(Vector2 localPoint)
    {
        base.PointerPressed(localPoint);
        if (!EnableAltScrub || !CanInteract || IsReadOnly || IsComposing ||
            UserInterfaceCanvas?.GetInputComponent()?.IsAltHeld != true ||
            !TryParseInRange(Text, out T current))
            return;
        _scrubStartText = Text;
        _scrubStartValue = current;
        _scrubStartX = localPoint.X;
        _scrubbing = true;
    }

    public override void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal)
    {
        base.MouseMoved(lastPosLocal, posLocal);
        if (!_scrubbing || !float.IsFinite(posLocal.X))
            return;
        float distance = (posLocal.X - _scrubStartX) / _scrubPixelsPerStep;
        int steps = (int)Math.Clamp(MathF.Truncate(distance), -1_000_000, 1_000_000);
        if (TryAddSteps(_scrubStartValue, steps, out T value))
            Text = value.ToString(null, Culture ?? CultureInfo.CurrentCulture);
    }

    public override void PointerReleased(Vector2 localPoint, bool inside)
    {
        base.PointerReleased(localPoint, inside);
        EndScrub(true);
    }

    public override void PointerCancelled()
    {
        base.PointerCancelled();
        EndScrub(false);
    }

    public override void UserSubmit()
    {
        if (_scrubbing)
        {
            _scrubbing = false;
            _scrubStartText = string.Empty;
        }
        base.UserSubmit();
    }

    public override void UserCancel()
    {
        EndScrub(false);
        base.UserCancel();
    }

    protected override void OnLostFocus()
    {
        EndScrub(false);
        base.OnLostFocus();
    }

    protected override void OnComponentDeactivated()
    {
        EndScrub(false);
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        _scrubbing = false;
        _scrubStartText = string.Empty;
        base.OnDestroying();
    }

    public override void CancelForRecycle()
    {
        EndScrub(false);
        base.CancelForRecycle();
    }

    private void EndScrub(bool commit)
    {
        if (!_scrubbing)
            return;
        _scrubbing = false;
        if (commit && CanInteract && !IsReadOnly)
            UserSubmit();
        else
        {
            Text = _scrubStartText;
            IsInvalid = false;
        }
        _scrubStartText = string.Empty;
    }

    public override bool TryConsumeFocusDirection(Vector2 direction)
    {
        if (float.IsFinite(direction.Y) && MathF.Abs(direction.Y) > MathF.Abs(direction.X))
            return TryStep(direction.Y > 0 ? 1 : -1);
        return base.TryConsumeFocusDirection(direction);
    }

    public override void RegisterInput(IInputRegistration input)
    {
        base.RegisterInput(input);
        input.RegisterKeyStateChange(EKey.Up, StepUp);
        input.RegisterKeyStateChange(EKey.Down, StepDown);
    }

    private void StepUp(bool pressed)
    {
        if (pressed)
            TryStep(1);
    }

    private void StepDown(bool pressed)
    {
        if (pressed)
            TryStep(-1);
    }

    protected override void ParseAndSet(PropertyInfo prop, object?[] targets)
    {
        if (!TryParseInRange(Text, out T value))
            return;
        foreach (object? target in targets)
            prop.SetValue(target, value);
    }

    private bool TryParseInRange(string text, out T value)
    {
        if (!T.TryParse(text, Culture ?? CultureInfo.CurrentCulture, out value) ||
            !T.IsFinite(value) || _minimum is { } minimum && value < minimum ||
            _maximum is { } maximum && value > maximum)
            return false;
        return true;
    }
}

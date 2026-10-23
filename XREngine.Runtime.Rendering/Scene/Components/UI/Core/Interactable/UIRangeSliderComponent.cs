using System.Numerics;
using XREngine.Input.Devices;
using XREngine.Input;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Edits a bounded scalar through keys or a captured pointer drag.</summary>
public class UIRangeSliderComponent : UIInteractableComponent
{
    private double _minimum;
    private double _maximum = 1;
    private double _step;
    private double _value;
    private double _dragStartValue;
    private bool _dragging;
    private IUIValueBinding<double>? _binding;

    public UIRangeSliderComponent()
    {
        NeedsMouseMove = true;
    }

    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.Slider;
    public override bool TryConsumeFocusDirection(Vector2 direction)
    {
        if (!CanInteract || IsReadOnly || !float.IsFinite(direction.X) ||
            MathF.Abs(direction.X) <= MathF.Abs(direction.Y))
            return false;
        AccessibilityAdjust(Math.Sign(direction.X));
        return true;
    }
    public override bool AccessibilityAdjust(int direction)
    {
        if (direction == 0 || !CanInteract || IsReadOnly)
            return false;
        double previous = Value;
        SetValue(Value + Math.Sign(direction) * (Step > 0 ? Step : (Maximum - Minimum) / 100),
            EUIValueChangeOrigin.User);
        return Value != previous;
    }
    public override string? AccessibilityValue => Value.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public double Minimum
    {
        get => _minimum;
        set
        {
            if (!double.IsFinite(value) || value > Maximum)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _minimum, value))
                SetValue(Value, EUIValueChangeOrigin.Programmatic);
        }
    }

    public double Maximum
    {
        get => _maximum;
        set
        {
            if (!double.IsFinite(value) || value < Minimum)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetField(ref _maximum, value))
                SetValue(Value, EUIValueChangeOrigin.Programmatic);
        }
    }

    public double Step
    {
        get => _step;
        set
        {
            if (!double.IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            SetField(ref _step, value);
        }
    }

    public double Value => _value;
    public double NormalizedValue => Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
    public bool IsDragging => _dragging;
    public event Action<UIValueChange<double>>? ValueChanged;
    public event Action<UIValueChange<double>>? ValuePreviewed;
    public event Action<UIValueChange<double>>? ValueCommitted;

    public IUIValueBinding<double>? Binding
    {
        get => _binding;
        set
        {
            if (ReferenceEquals(_binding, value))
                return;
            if (IsActiveInHierarchy && _binding is not null)
                _binding.ValueChanged -= OnBindingChanged;
            SetField(ref _binding, value);
            if (IsActiveInHierarchy && _binding is not null)
                _binding.ValueChanged += OnBindingChanged;
            if (_binding is not null)
                SetValue(_binding.Value, EUIValueChangeOrigin.Binding);
        }
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (_binding is not null)
        {
            _binding.ValueChanged += OnBindingChanged;
            SetValue(_binding.Value, EUIValueChangeOrigin.Binding);
        }
    }

    protected override void OnComponentDeactivated()
    {
        EndDrag(false);
        if (_binding is not null)
            _binding.ValueChanged -= OnBindingChanged;
        base.OnComponentDeactivated();
    }

    private void OnBindingChanged(double value)
    {
        if (!_dragging)
            SetValue(value, EUIValueChangeOrigin.Binding);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        if (propName is (nameof(Value) or nameof(Minimum) or nameof(Maximum) or nameof(Step)) &&
            SceneNode is not null && !SceneNode.IsTransformNull)
            UITransform.RecordUIChange(UIChangeDomain.Visual);
    }

    private double Normalize(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        value = Math.Clamp(value, Minimum, Maximum);
        if (Step > 0)
            value = Math.Clamp(Minimum + Math.Round((value - Minimum) / Step) * Step, Minimum, Maximum);
        return value;
    }

    public void SetValue(double value, EUIValueChangeOrigin origin = EUIValueChangeOrigin.Programmatic)
    {
        value = Normalize(value);
        if (_value == value)
            return;
        double previous = _value;
        if (_binding is not null && origin != EUIValueChangeOrigin.Binding && !_dragging)
        {
            _binding.SetValue(value);
            value = Normalize(_binding.Value);
        }
        SetField(ref _value, value, nameof(Value));
        ValueChanged?.Invoke(new(previous, value, origin));
        if (_dragging)
            ValuePreviewed?.Invoke(new(previous, value, origin));
        else if (origin == EUIValueChangeOrigin.User)
            ValueCommitted?.Invoke(new(previous, value, origin));
    }

    public bool BeginDrag(float normalized)
    {
        if (!CanInteract || IsReadOnly)
            return false;
        _dragStartValue = Value;
        _dragging = true;
        UpdateDrag(normalized);
        return true;
    }

    public void UpdateDrag(float normalized)
    {
        if (!_dragging || !float.IsFinite(normalized))
            return;
        SetValue(Minimum + Math.Clamp(normalized, 0, 1) * (Maximum - Minimum), EUIValueChangeOrigin.User);
    }

    public void EndDrag(bool commit)
    {
        if (!_dragging)
            return;
        _dragging = false;
        double previous = _dragStartValue;
        if (!commit || !CanInteract || IsReadOnly)
        {
            SetValue(_binding?.Value ?? previous, EUIValueChangeOrigin.Binding);
            return;
        }
        if (_binding is not null)
        {
            _binding.SetValue(_value);
            SetField(ref _value, Normalize(_binding.Value), nameof(Value));
        }
        if (previous != _value)
            ValueCommitted?.Invoke(new(previous, _value, EUIValueChangeOrigin.User));
    }

    public override void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal)
    {
        base.MouseMoved(lastPosLocal, posLocal);
        if (_dragging && BoundableTransform.ActualWidth > 0)
            UpdateDrag(posLocal.X / BoundableTransform.ActualWidth);
    }

    public override void PointerPressed(Vector2 localPoint)
    {
        base.PointerPressed(localPoint);
        if (BoundableTransform.ActualWidth > 0)
            BeginDrag(localPoint.X / BoundableTransform.ActualWidth);
    }

    public override void PointerReleased(Vector2 localPoint, bool inside)
    {
        base.PointerReleased(localPoint, inside);
        if (inside && BoundableTransform.ActualWidth > 0)
            UpdateDrag(localPoint.X / BoundableTransform.ActualWidth);
        EndDrag(inside);
    }

    public override void PointerCancelled()
    {
        base.PointerCancelled();
        EndDrag(false);
    }

    public override void RegisterInput(IInputRegistration input)
    {
        base.RegisterInput(input);
        input.RegisterKeyStateChange(EKey.Left, Decrement);
        input.RegisterKeyStateChange(EKey.Down, Decrement);
        input.RegisterKeyStateChange(EKey.Right, Increment);
        input.RegisterKeyStateChange(EKey.Up, Increment);
    }

    private void Decrement(bool pressed)
    {
        if (pressed && CanInteract && !IsReadOnly)
            SetValue(Value - (Step > 0 ? Step : (Maximum - Minimum) / 100), EUIValueChangeOrigin.User);
    }

    private void Increment(bool pressed)
    {
        if (pressed && CanInteract && !IsReadOnly)
            SetValue(Value + (Step > 0 ? Step : (Maximum - Minimum) / 100), EUIValueChangeOrigin.User);
    }

    public override void CancelForRecycle()
    {
        EndDrag(false);
        Binding = null;
        base.CancelForRecycle();
    }
}

using XREngine.Input.Devices;
using XREngine.Input;

namespace XREngine.Rendering.UI;

/// <summary>A native button that selects a stable option in a radio group.</summary>
public class UIRadioButtonComponent : UIButtonComponent
{
    private UIRadioGroupState? _group;
    private ulong _optionId;

    public UIRadioButtonComponent()
    {
        InteractAction += _ => Group?.Select(OptionId);
    }

    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.RadioButton;
    public override string? AccessibilityValue => IsSelected ? "Selected" : "Not selected";

    public ulong OptionId
    {
        get => _optionId;
        set
        {
            if (_optionId == value)
                return;
            if (IsActiveInHierarchy)
                _group?.Unregister(this);
            SetField(ref _optionId, value);
            if (IsActiveInHierarchy)
                _group?.Register(this);
        }
    }

    public UIRadioGroupState? Group
    {
        get => _group;
        set
        {
            if (ReferenceEquals(_group, value))
                return;
            if (IsActiveInHierarchy)
                _group?.Unregister(this);
            SetField(ref _group, value);
            if (IsActiveInHierarchy)
                _group?.Register(this);
        }
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _group?.Register(this);
    }

    protected override void OnComponentDeactivated()
    {
        _group?.Unregister(this);
        base.OnComponentDeactivated();
    }

    public override void CancelForRecycle()
    {
        Group = null;
        base.CancelForRecycle();
    }

    public override void RegisterInput(IInputRegistration input)
    {
        base.RegisterInput(input);
        input.RegisterKeyStateChange(EKey.Left, SelectPrevious);
        input.RegisterKeyStateChange(EKey.Up, SelectPrevious);
        input.RegisterKeyStateChange(EKey.Right, SelectNext);
        input.RegisterKeyStateChange(EKey.Down, SelectNext);
    }

    private void SelectPrevious(bool pressed)
    {
        if (pressed)
            _group?.SelectAdjacent(this, -1);
    }

    private void SelectNext(bool pressed)
    {
        if (pressed)
            _group?.SelectAdjacent(this, 1);
    }
}

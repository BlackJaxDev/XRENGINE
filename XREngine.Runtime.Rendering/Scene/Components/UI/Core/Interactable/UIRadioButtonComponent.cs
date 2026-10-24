using System.Numerics;
using XREngine.Input.Devices;
using XREngine.Input;

namespace XREngine.Rendering.UI;

/// <summary>A native button that selects a stable option in a radio group.</summary>
public class UIRadioButtonComponent : UIButtonComponent
{
    private UIRadioGroupState? _group;
    private UIRadioGroupState? _requestedGroup;
    private UIRadioGroupState? _ownedGroup;
    private UIRadioGroupState? _registeredGroup;
    private ulong _optionId;
    private ulong _requestedOptionId;
    private bool _terminal;
    private uint _groupRevision;
    private uint _optionRevision;

    public UIRadioButtonComponent()
    {
        InteractAction += _ =>
        {
            if (!_terminal && IsActiveInHierarchy && !IsDestroyed && !IsDestroyQueued)
                _group?.Select(OptionId);
        };
    }

    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.RadioButton;
    public override string? AccessibilityValue => IsSelected ? "Selected" : "Not selected";
    public override bool AccessibilityAdjust(int direction)
        => !_terminal && direction != 0 && (_group?.SelectAdjacent(this, Math.Sign(direction)) ?? false);
    public override bool TryConsumeFocusDirection(Vector2 direction)
    {
        if (_terminal || !CanInteract || IsReadOnly || !float.IsFinite(direction.X) || !float.IsFinite(direction.Y) ||
            direction == Vector2.Zero)
            return false;
        float axis = MathF.Abs(direction.X) >= MathF.Abs(direction.Y) ? direction.X : -direction.Y;
        return _group?.SelectAdjacent(this, Math.Sign(axis)) ?? false;
    }

    public ulong OptionId
    {
        get => _optionId;
        set
        {
            if (_terminal || _optionId == value)
                return;
            uint revision = ++_optionRevision;
            _requestedOptionId = value;
            Exception? failure = ReleaseRegistration();
            try { SetField(ref _optionId, value); }
            catch (Exception error) { failure = Combine(failure, error); }
            if (revision == _optionRevision)
                _requestedOptionId = _optionId;
            else
                SetField(ref _optionId, _requestedOptionId, publishNotifications: false);
            if (IsActiveInHierarchy && !_terminal && !IsDestroyed && !IsDestroyQueued)
                try { AttachRegistration(); }
                catch (Exception error) { failure = Combine(failure, error); }
            if (failure is not null)
                throw failure;
        }
    }

    public UIRadioGroupState? Group
    {
        get => _group;
        set
        {
            if (_terminal || ReferenceEquals(_group, value))
                return;
            uint revision = ++_groupRevision;
            _requestedGroup = value;
            Exception? failure = ReleaseRegistration();
            if (_ownedGroup is { } previous)
            {
                _ownedGroup = null;
                previous.DetachOwner(this);
            }
            try { SetField(ref _group, value); }
            catch (Exception error) { failure = Combine(failure, error); }
            if (revision == _groupRevision)
                _requestedGroup = _group;
            else
                SetField(ref _group, _requestedGroup, publishNotifications: false);
            try { ReconcileGroupOwnership(); }
            catch (Exception error) { failure = Combine(failure, error); }
            if (failure is not null)
                throw failure;
        }
    }

    internal bool DetachDisposedGroup(UIRadioGroupState group)
    {
        if (!ReferenceEquals(_group, group) && !ReferenceEquals(_registeredGroup, group))
            return false;
        ++_groupRevision;
        if (ReferenceEquals(_registeredGroup, group))
            _registeredGroup = null;
        if (ReferenceEquals(_group, group))
        {
            SetField(ref _group, null, publishNotifications: false);
            _requestedGroup = null;
        }
        if (ReferenceEquals(_ownedGroup, group))
            _ownedGroup = null;
        return true;
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (!_terminal && IsActiveInHierarchy && !IsDestroyed && !IsDestroyQueued)
            AttachRegistration();
    }

    protected override void OnComponentDeactivated()
    {
        try
        {
            Exception? failure = ReleaseRegistration();
            if (failure is not null)
                throw failure;
        }
        finally { base.OnComponentDeactivated(); }
    }

    protected override void OnDestroying()
    {
        _terminal = true;
        ++_groupRevision;
        ++_optionRevision;
        _requestedGroup = null;
        SetField(ref _group, null, publishNotifications: false);
        UIRadioGroupState? group = _ownedGroup;
        _ownedGroup = null;
        group?.DetachOwner(this);
        try
        {
            Exception? failure = ReleaseRegistration();
            if (failure is not null)
                throw failure;
        }
        finally { base.OnDestroying(); }
    }

    public override void CancelForRecycle()
    {
        try { Group = null; }
        finally { base.CancelForRecycle(); }
    }

    public override void RegisterInput(IInputRegistration input)
    {
        base.RegisterInput(input);
        input.RegisterKeyStateChange(EKey.Left, SelectPrevious);
        input.RegisterKeyStateChange(EKey.Up, SelectPrevious);
        input.RegisterKeyStateChange(EKey.Right, SelectNext);
        input.RegisterKeyStateChange(EKey.Down, SelectNext);
    }

    private void AttachRegistration()
    {
        UIRadioGroupState? group = _group;
        if (_terminal || !IsActiveInHierarchy || IsDestroyed || IsDestroyQueued ||
            group is null || _optionId == 0 || ReferenceEquals(_registeredGroup, group))
            return;
        _registeredGroup = group;
        try { group.Register(this); }
        catch
        {
            if (ReferenceEquals(_registeredGroup, group))
                _registeredGroup = null;
            group.Unregister(this);
            throw;
        }
    }

    private void ReconcileGroupOwnership()
    {
        UIRadioGroupState? group = _group;
        if (ReferenceEquals(_ownedGroup, group))
        {
            if (IsActiveInHierarchy && !_terminal && !IsDestroyed && !IsDestroyQueued)
                AttachRegistration();
            return;
        }
        UIRadioGroupState? old = _ownedGroup;
        _ownedGroup = null;
        old?.DetachOwner(this);
        if (group is null || _terminal || IsDestroyed || IsDestroyQueued)
            return;
        try
        {
            group.AttachOwner(this);
            _ownedGroup = group;
            if (IsActiveInHierarchy)
                AttachRegistration();
        }
        catch
        {
            if (ReferenceEquals(_ownedGroup, group))
                _ownedGroup = null;
            group.DetachOwner(this);
            if (ReferenceEquals(_group, group))
                SetField(ref _group, null, publishNotifications: false);
            throw;
        }
    }

    private Exception? ReleaseRegistration()
    {
        UIRadioGroupState? group = _registeredGroup;
        _registeredGroup = null;
        if (group is null)
            return null;
        try { group.Unregister(this); return null; }
        catch (Exception error) { return error; }
    }

    private static Exception Combine(Exception? first, Exception second)
        => first is null ? second : new AggregateException(first, second);

    private void SelectPrevious(bool pressed)
    {
        if (pressed && !_terminal && IsActiveInHierarchy)
            _group?.SelectAdjacent(this, -1);
    }

    private void SelectNext(bool pressed)
    {
        if (pressed && !_terminal && IsActiveInHierarchy)
            _group?.SelectAdjacent(this, 1);
    }
}

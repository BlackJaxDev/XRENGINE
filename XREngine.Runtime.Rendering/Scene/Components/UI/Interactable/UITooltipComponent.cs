using System.Diagnostics;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

/// <summary>Shows a passive, accepted-layout hint after a target is hovered or focused.</summary>
public sealed class UITooltipComponent : UIComponent
{
    private UIInteractableComponent? _target;
    private UIContextMenuComponent? _popup;
    private string _text = string.Empty;
    private bool _ownsDescription;
    private bool _waiting;
    private bool _showing;
    private long _triggeredAt;

    public UIInteractableComponent? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value))
                return;
            if (IsActiveInHierarchy)
                DetachTarget();
            SetField(ref _target, value);
            if (IsActiveInHierarchy)
                AttachTarget();
        }
    }

    /// <summary>A passive popup node attached to the same canvas as the target.</summary>
    public UIContextMenuComponent? Popup
    {
        get => _popup;
        set
        {
            if (ReferenceEquals(_popup, value))
                return;
            Hide();
            if (IsActiveInHierarchy && _popup is not null)
                _popup.Dismissed -= OnPopupDismissed;
            SetField(ref _popup, value);
            if (IsActiveInHierarchy && _popup is not null)
                _popup.Dismissed += OnPopupDismissed;
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            string old = _text;
            if (!SetField(ref _text, value ?? string.Empty))
                return;
            if (_ownsDescription && _target?.AccessibilityDescription == old)
                _target.AccessibilityDescription = _text;
            else if (IsActiveInHierarchy && _target is { AccessibilityDescription: null } target && _text.Length > 0)
            {
                target.AccessibilityDescription = _text;
                _ownsDescription = true;
            }
            if (_showing)
            {
                Hide();
                StartWait();
            }
        }
    }

    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(500);

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _target ??= GetSiblingComponent<UIInteractableComponent>();
        AttachTarget();
        if (_popup is not null)
            _popup.Dismissed += OnPopupDismissed;
    }

    protected override void OnComponentDeactivated()
    {
        Hide();
        DetachTarget();
        if (_popup is not null)
            _popup.Dismissed -= OnPopupDismissed;
        base.OnComponentDeactivated();
    }

    private void AttachTarget()
    {
        if (_target is null)
            return;
        _target.MouseOverlapEnter += OnTargetEntered;
        _target.MouseOverlapLeave += OnTargetLeft;
        _target.GotFocus += OnTargetEntered;
        _target.LostFocus += OnTargetLeft;
        _target.PropertyChanged += OnTargetPropertyChanged;
        if (_target.AccessibilityDescription is null && _text.Length > 0)
        {
            _target.AccessibilityDescription = _text;
            _ownsDescription = true;
        }
        if (_target.IsMouseOver || _target.IsFocused)
            StartWait();
    }

    private void DetachTarget()
    {
        Hide();
        if (_target is null)
            return;
        _target.MouseOverlapEnter -= OnTargetEntered;
        _target.MouseOverlapLeave -= OnTargetLeft;
        _target.GotFocus -= OnTargetEntered;
        _target.LostFocus -= OnTargetLeft;
        _target.PropertyChanged -= OnTargetPropertyChanged;
        if (_ownsDescription && _target.AccessibilityDescription == _text)
            _target.AccessibilityDescription = null;
        _ownsDescription = false;
    }

    private void OnTargetEntered(UIInteractableComponent _) => StartWait();

    private void OnTargetPropertyChanged(object? sender, IXRPropertyChangedEventArgs change)
    {
        if (change.PropertyName == nameof(UIInteractableComponent.IsEnabled) &&
            _target?.IsEnabled == false)
            Hide();
    }

    private void OnTargetLeft(UIInteractableComponent _)
    {
        if (_target?.IsMouseOver == true && _target.IsFocused)
            return;
        // MouseOverlapLeave fires before the hover flag changes, while LostFocus
        // fires before the focus flag changes.
        Hide();
    }

    private void StartWait()
    {
        if (_waiting || _showing || _target is null || _popup is null ||
            _text.Length == 0 || !_target.CanInteract)
            return;
        _triggeredAt = Stopwatch.GetTimestamp();
        _waiting = true;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, TickTooltip);
    }

    private void TickTooltip()
    {
        if (_target is null || !_target.CanInteract ||
            (!_target.IsMouseOver && !_target.IsFocused) || _popup is null)
        {
            Hide();
            return;
        }
        if (Stopwatch.GetElapsedTime(_triggeredAt) < Delay)
            return;
        StopWait();
        UIBoundableTransform? parent = _popup.TransformAs<UIBoundableTransform>(true)?.Parent as UIBoundableTransform;
        if (parent is null || !ReferenceEquals(_target.UserInterfaceCanvas, _popup.UserInterfaceCanvas))
            return;
        Vector2 canvasPosition = _target.BoundableTransform.LocalToCanvas(
            new Vector2(_target.BoundableTransform.ActualWidth + 8, _target.BoundableTransform.ActualHeight));
        Vector2 localPosition = parent.CanvasToLocal(canvasPosition);
        string label = _text;
        _popup.PreferredWidth = Math.Clamp(label.Length * 7 + 16, 80, 360);
        _popup.ShowPassiveContent(localPosition, 24, list =>
        {
            SceneNode line = new(list) { Name = "Tooltip text" };
            UIBoundableTransform bounds = line.SetTransform<UIBoundableTransform>();
            bounds.MinAnchor = Vector2.Zero;
            bounds.MaxAnchor = new Vector2(1, 0);
            bounds.Height = 24;
            line.NewChild<UITextComponent>(out UITextComponent text);
            text.Text = label;
            text.FontSize = 12;
            text.Color = ColorF4.White;
            text.VerticalAlignment = EVerticalAlignment.Center;
            text.BoundableTransform.Margins = new Vector4(6, 0, 6, 0);
        });
        _showing = _popup.IsOpen;
    }

    private void StopWait()
    {
        if (!_waiting)
            return;
        _waiting = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, TickTooltip);
    }

    private void Hide()
    {
        StopWait();
        if (_showing)
        {
            _showing = false;
            _popup?.Hide();
        }
    }

    private void OnPopupDismissed() => _showing = false;
}

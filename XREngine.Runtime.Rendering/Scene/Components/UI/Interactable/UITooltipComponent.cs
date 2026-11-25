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
    private bool _tearingDown;
    private ulong _targetRevision;
    private ulong _popupRevision;
    private ulong _textRevision;
    private long _triggeredAt;

    public UIInteractableComponent? Target
    {
        get => _target;
        set
        {
            if (_tearingDown || IsDestroyed)
                return;
            if (ReferenceEquals(_target, value))
                return;
            ulong revision = ++_targetRevision;
            UIInteractableComponent? previous = _target;
            if (IsActiveInHierarchy)
                DetachTarget();
            if (!OwnsTargetChange(revision))
                return;
            bool changed = SetField(ref _target, value);
            if (!changed && OwnsTargetChange(revision) &&
                ReferenceEquals(_target, previous) && IsActiveInHierarchy)
                AttachTarget();
            if (!OwnsTargetChange(revision) || !ReferenceEquals(_target, value))
                return;
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
            if (_tearingDown || IsDestroyed)
                return;
            if (ReferenceEquals(_popup, value))
                return;
            ulong revision = ++_popupRevision;
            Hide();
            if (!OwnsPopupChange(revision))
                return;
            UIContextMenuComponent? previous = _popup;
            if (IsActiveInHierarchy && previous is not null)
                previous.Dismissed -= OnPopupDismissed;
            if (!OwnsPopupChange(revision))
                return;
            bool changed = SetField(ref _popup, value);
            if (!changed && OwnsPopupChange(revision) &&
                ReferenceEquals(_popup, previous) && IsActiveInHierarchy && previous is not null)
                previous.Dismissed += OnPopupDismissed;
            if (!OwnsPopupChange(revision) || !ReferenceEquals(_popup, value))
                return;
            if (IsActiveInHierarchy && _popup is not null)
                _popup.Dismissed += OnPopupDismissed;
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_tearingDown || IsDestroyed)
                return;
            ulong revision = ++_textRevision;
            string old = _text;
            if (!SetField(ref _text, value ?? string.Empty))
                return;
            if (!OwnsTextChange(revision))
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

    protected override void OnDestroying()
    {
        _tearingDown = true;
        if (_popup is not null)
            _popup.Dismissed -= OnPopupDismissed;
        try
        {
            DetachTarget();
        }
        finally
        {
            _target = null;
            _popup = null;
            base.OnDestroying();
        }
    }

    protected override bool OnPropertyChanging<T>(string? propName, T field, T value)
    {
        if (_tearingDown || IsDestroyed)
            return false;
        ulong targetRevision = _targetRevision;
        ulong popupRevision = _popupRevision;
        ulong textRevision = _textRevision;
        bool allowed = base.OnPropertyChanging(propName, field, value);
        return allowed && !_tearingDown && !IsDestroyed && (propName switch
        {
            nameof(Target) => targetRevision == _targetRevision,
            nameof(Popup) => popupRevision == _popupRevision,
            nameof(Text) => textRevision == _textRevision,
            _ => true
        });
    }

    private bool OwnsTargetChange(ulong revision)
        => !_tearingDown && !IsDestroyed && revision == _targetRevision;

    private bool OwnsPopupChange(ulong revision)
        => !_tearingDown && !IsDestroyed && revision == _popupRevision;

    private bool OwnsTextChange(ulong revision)
        => !_tearingDown && !IsDestroyed && revision == _textRevision;

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
        UIInteractableComponent? target = _target;
        if (target is null)
        {
            Hide();
            return;
        }
        target.MouseOverlapEnter -= OnTargetEntered;
        target.MouseOverlapLeave -= OnTargetLeft;
        target.GotFocus -= OnTargetEntered;
        target.LostFocus -= OnTargetLeft;
        target.PropertyChanged -= OnTargetPropertyChanged;
        if (_ownsDescription && !target.IsDestroyed && target.AccessibilityDescription == _text)
            target.AccessibilityDescription = null;
        _ownsDescription = false;
        Hide();
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
        if (_tearingDown || IsDestroyed || _waiting || _showing || _target is null ||
            _popup is null || _popup.IsDestroyed ||
            _text.Length == 0 || !_target.CanInteract)
            return;
        _triggeredAt = Stopwatch.GetTimestamp();
        _waiting = true;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, TickTooltip);
    }

    private void TickTooltip()
    {
        if (_tearingDown || IsDestroyed || _target is null || !_target.CanInteract ||
            (!_target.IsMouseOver && !_target.IsFocused) || _popup is null || _popup.IsDestroyed)
        {
            Hide();
            return;
        }
        if (Stopwatch.GetElapsedTime(_triggeredAt) < Delay)
            return;
        StopWait();
        UIContextMenuComponent popup = _popup;
        UIBoundableTransform? parent = popup.TransformAs<UIBoundableTransform>(true)?.Parent as UIBoundableTransform;
        if (parent is null || !ReferenceEquals(_target.UserInterfaceCanvas, popup.UserInterfaceCanvas))
            return;
        Vector2 canvasPosition = _target.BoundableTransform.LocalToCanvas(
            new Vector2(_target.BoundableTransform.ActualWidth + 8, _target.BoundableTransform.ActualHeight));
        Vector2 localPosition = parent.CanvasToLocal(canvasPosition);
        string label = _text;
        popup.PreferredWidth = Math.Clamp(label.Length * 7 + 16, 80, 360);
        popup.ShowPassiveOwnedContent(localPosition, 24, builder =>
        {
            builder.Add(builder.Root, "Tooltip text", new UIBoundableTransform(), leaf =>
            {
                leaf.Write(() => leaf.Transform.MinAnchor = Vector2.Zero);
                leaf.Write(() => leaf.Transform.MaxAnchor = new Vector2(1, 0));
                leaf.Write(() => leaf.Transform.Height = 24);
                UITextComponent text = leaf.Add(() => new UITextComponent());
                leaf.Write(() => text.Text = label);
                leaf.Write(() => text.FontSize = 12);
                leaf.Write(() => text.Color = ColorF4.White);
                leaf.Write(() => text.VerticalAlignment = EVerticalAlignment.Center);
                leaf.Write(() => text.BoundableTransform.Margins = new Vector4(6, 0, 6, 0));
            });
        });
        if (!_tearingDown && !IsDestroyed && ReferenceEquals(_popup, popup))
            _showing = popup.IsOpen;
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
            if (_popup is { IsDestroyed: false } popup)
                popup.Hide();
        }
    }

    private void OnPopupDismissed() => _showing = false;
}

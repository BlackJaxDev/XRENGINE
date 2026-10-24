using System.Globalization;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

/// <summary>Shows a color swatch and manages preview, commit, and cancel.</summary>
public class UIColorSelectorComponent : UIButtonComponent
{
    private ColorF4 _linearColor = ColorF4.White;
    private ColorF4 _editOriginal;
    private bool _editing;
    private IUIValueBinding<ColorF4>? _binding;
    private IUIValueBinding<ColorF4>? _subscribedBinding;
    private Action<ColorF4>? _subscribedBindingHandler;
    private IUIValueBinding<ColorF4>? _requestedBinding;
    private long _bindingRevision;
    private UIContextMenuComponent? _popup;
    private UIContextMenuComponent? _subscribedPopup;
    private Action? _subscribedPopupHandler;
    private UIContextMenuComponent? _requestedPopup;
    private long _popupRevision;
    private bool _ownerActive;
    private bool _terminal;
    private bool _deactivating;
    private bool _activationPending;

    private static readonly (string Name, string Hex)[] Palette =
    [
        ("White", "#FFFFFFFF"), ("Black", "#000000FF"),
        ("Red", "#FF0000FF"), ("Green", "#00FF00FF"),
        ("Blue", "#0000FFFF"), ("Yellow", "#FFFF00FF"),
        ("Cyan", "#00FFFFFF"), ("Magenta", "#FF00FFFF"),
        ("Transparent", "#00000000")
    ];

    public UIColorSelectorComponent()
    {
        DefaultBackgroundColor = _linearColor;
        InteractAction += _ => OpenPalette();
    }

    public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.ColorWell;
    public override string? AccessibilityValue => ToHexSrgb(_linearColor);
    public ColorF4 LinearColor => _linearColor;
    public bool IsEditing => _editing;
    /// <summary>Canvas-attached popup used for the built-in sRGB palette.</summary>
    public UIContextMenuComponent? Popup
    {
        get => _popup;
        set
        {
            if (_terminal || IsDestroyed || IsDestroyQueued)
            {
                if (value is not null)
                    throw new ObjectDisposedException(nameof(UIColorSelectorComponent));
                return;
            }
            if (ReferenceEquals(_popup, value))
                return;
            long revision = ++_popupRevision;
            _requestedPopup = value;
            UIContextMenuComponent? previous = _popup;
            DetachPopup();
            previous?.Hide();
            CancelEdit();
            if (!OwnsPopupRevision(revision))
            {
                if (!_terminal && !IsDestroyed && !IsDestroyQueued)
                    SetField(ref _popup, _requestedPopup, publishNotifications: false);
                return;
            }
            try { SetField(ref _popup, value); }
            finally
            {
                if (!OwnsPopupRevision(revision))
                    SetField(ref _popup, _requestedPopup, publishNotifications: false);
                else if (_ownerActive)
                    AttachPopup(_popup, revision);
            }
            if (OwnsPopupRevision(revision))
                _requestedPopup = _popup;
        }
    }

    public Vector2 PopupPosition { get; set; }
    public event Action<UIColorSelectorComponent>? EditingStarted;
    public event Action<UIValueChange<ColorF4>>? ColorPreviewed;
    public event Action<UIValueChange<ColorF4>>? ColorCommitted;

    public IUIValueBinding<ColorF4>? Binding
    {
        get => _binding;
        set
        {
            if (_terminal || IsDestroyed || IsDestroyQueued)
            {
                if (value is not null)
                    throw new ObjectDisposedException(nameof(UIColorSelectorComponent));
                return;
            }
            if (ReferenceEquals(_binding, value))
                return;
            long revision = ++_bindingRevision;
            _requestedBinding = value;
            DetachBinding();
            if (!OwnsBindingRevision(revision))
            {
                if (!_terminal && !IsDestroyed && !IsDestroyQueued)
                    SetField(ref _binding, _requestedBinding, publishNotifications: false);
                return;
            }
            try { SetField(ref _binding, value); }
            finally
            {
                if (!OwnsBindingRevision(revision))
                    SetField(ref _binding, _requestedBinding, publishNotifications: false);
                else if (_ownerActive)
                    AttachBinding(_binding, revision);
            }
            if (!OwnsBindingRevision(revision))
                return;
            _requestedBinding = _binding;
            SynchronizeBinding();
        }
    }

    private bool OwnsBindingRevision(long revision)
        => !_terminal && !IsDestroyed && !IsDestroyQueued && _bindingRevision == revision;

    private bool OwnsPopupRevision(long revision)
        => !_terminal && !IsDestroyed && !IsDestroyQueued && _popupRevision == revision;

    private void AttachBinding(IUIValueBinding<ColorF4>? source, long revision)
    {
        if (source is null || !_ownerActive || !OwnsBindingRevision(revision) ||
            ReferenceEquals(_subscribedBinding, source))
            return;
        Action<ColorF4> handler = value => OnBindingChanged(source, revision, value);
        _subscribedBinding = source;
        _subscribedBindingHandler = handler;
        try { source.ValueChanged += handler; }
        catch
        {
            if (ReferenceEquals(_subscribedBindingHandler, handler))
            {
                _subscribedBinding = null;
                _subscribedBindingHandler = null;
            }
            source.ValueChanged -= handler;
            throw;
        }
        if (!_ownerActive || !OwnsBindingRevision(revision) ||
            !ReferenceEquals(_binding, source) || !ReferenceEquals(_subscribedBindingHandler, handler))
            source.ValueChanged -= handler;
    }

    private void DetachBinding()
    {
        IUIValueBinding<ColorF4>? source = _subscribedBinding;
        Action<ColorF4>? handler = _subscribedBindingHandler;
        _subscribedBinding = null;
        _subscribedBindingHandler = null;
        if (source is not null && handler is not null)
            source.ValueChanged -= handler;
    }

    private void AttachPopup(UIContextMenuComponent? popup, long revision)
    {
        if (popup is null || !_ownerActive || !OwnsPopupRevision(revision) ||
            ReferenceEquals(_subscribedPopup, popup))
            return;
        Action handler = () => OnPopupDismissed(popup, revision);
        _subscribedPopup = popup;
        _subscribedPopupHandler = handler;
        try { popup.Dismissed += handler; }
        catch
        {
            if (ReferenceEquals(_subscribedPopupHandler, handler))
            {
                _subscribedPopup = null;
                _subscribedPopupHandler = null;
            }
            popup.Dismissed -= handler;
            throw;
        }
        if (!_ownerActive || !OwnsPopupRevision(revision) ||
            !ReferenceEquals(_popup, popup) || !ReferenceEquals(_subscribedPopupHandler, handler))
            popup.Dismissed -= handler;
    }

    private void DetachPopup()
    {
        UIContextMenuComponent? popup = _subscribedPopup;
        Action? handler = _subscribedPopupHandler;
        _subscribedPopup = null;
        _subscribedPopupHandler = null;
        if (popup is not null && handler is not null)
            popup.Dismissed -= handler;
    }

    private void SynchronizeBinding()
    {
        IUIValueBinding<ColorF4>? source = _binding;
        long revision = _bindingRevision;
        if (source is null)
            return;
        ColorF4 value = source.Value;
        if (!_deactivating && OwnsBindingRevision(revision) &&
            ReferenceEquals(_binding, source) && (!_ownerActive || IsActiveInHierarchy))
            ApplyLinear(value);
    }

    protected override void OnComponentActivated()
    {
        long bindingRevision = _bindingRevision;
        long popupRevision = _popupRevision;
        base.OnComponentActivated();
        if (_deactivating)
        {
            _activationPending = true;
            return;
        }
        if (_terminal || IsDestroyed || IsDestroyQueued || !IsActiveInHierarchy ||
            bindingRevision != _bindingRevision || popupRevision != _popupRevision)
            return;
        _ownerActive = true;
        AttachPopup(_popup, _popupRevision);
        if (!_ownerActive || !IsActiveInHierarchy || !OwnsPopupRevision(popupRevision))
            return;
        AttachBinding(_binding, _bindingRevision);
        if (_ownerActive && IsActiveInHierarchy && OwnsBindingRevision(bindingRevision))
            SynchronizeBinding();
    }

    protected override void OnComponentDeactivated()
    {
        _ownerActive = false;
        ++_popupRevision;
        ++_bindingRevision;
        _deactivating = true;
        _activationPending = false;
        UIContextMenuComponent? popup = _popup;
        Exception? failure = null;
        try { DetachPopup(); }
        catch (Exception error) { failure = error; }
        try { DetachBinding(); }
        catch (Exception error) { failure = Combine(failure, error); }
        try { popup?.Hide(); }
        catch (Exception error) { failure = Combine(failure, error); }
        try { CancelEdit(); }
        catch (Exception error) { failure = Combine(failure, error); }
        try { base.OnComponentDeactivated(); }
        catch (Exception error) { failure = Combine(failure, error); }
        _deactivating = false;
        if (_activationPending && !_terminal && IsActiveInHierarchy)
        {
            _activationPending = false;
            _ownerActive = true;
            try { AttachPopup(_popup, _popupRevision); }
            catch (Exception error) { failure = Combine(failure, error); }
            try { AttachBinding(_binding, _bindingRevision); }
            catch (Exception error) { failure = Combine(failure, error); }
            try { SynchronizeBinding(); }
            catch (Exception error) { failure = Combine(failure, error); }
        }
        if (failure is not null)
            throw failure;
    }

    protected override void OnDestroying()
    {
        _terminal = true;
        _ownerActive = false;
        _activationPending = false;
        ++_popupRevision;
        ++_bindingRevision;
        _requestedBinding = null;
        _requestedPopup = null;
        IUIValueBinding<ColorF4>? source = _binding;
        UIContextMenuComponent? popup = _popup;
        bool wasEditing = _editing;
        _editing = false;
        SetField(ref _binding, null, publishNotifications: false);
        SetField(ref _popup, null, publishNotifications: false);
        Exception? failure = null;
        try { DetachPopup(); }
        catch (Exception error) { failure = error; }
        try { DetachBinding(); }
        catch (Exception error) { failure = Combine(failure, error); }
        if (wasEditing)
        {
            try
            {
                ColorF4 rollback = source?.Value ?? _editOriginal;
                if (IsFinite(rollback))
                    SetField(ref _linearColor, rollback, publishNotifications: false,
                        nameof(LinearColor));
            }
            catch (Exception error) { failure = Combine(failure, error); }
        }
        try { popup?.Hide(); }
        catch (Exception error) { failure = Combine(failure, error); }
        try { base.OnDestroying(); }
        catch (Exception error) { failure = Combine(failure, error); }
        if (failure is not null)
            throw failure;
    }

    private static Exception Combine(Exception? first, Exception next)
        => first is null ? next : new AggregateException(first, next);

    private void OnBindingChanged(IUIValueBinding<ColorF4> source, long revision, ColorF4 color)
    {
        if (_ownerActive && OwnsBindingRevision(revision) &&
            ReferenceEquals(_subscribedBinding, source) && !_editing)
            ApplyLinear(color);
    }

    private void ApplyLinear(ColorF4 color)
    {
        if (!IsFinite(color))
            throw new ArgumentOutOfRangeException(nameof(color));
        SetField(ref _linearColor, color, nameof(LinearColor));
        DefaultBackgroundColor = color;
    }

    public void SetColor(ColorF4 color, EUIColorSpace colorSpace)
    {
        if (_terminal || IsDestroyed || IsDestroyQueued)
            return;
        ColorF4 linear = colorSpace == EUIColorSpace.SRgb ? SrgbToLinear(color) : color;
        ApplyLinear(linear);
        if (_terminal || IsDestroyed || IsDestroyQueued)
            return;
        if (!_editing && _binding is not null)
        {
            IUIValueBinding<ColorF4> source = _binding;
            long revision = _bindingRevision;
            source.SetValue(linear);
            if (!OwnsBindingRevision(revision) || !ReferenceEquals(_binding, source))
                return;
            ApplyLinear(source.Value);
        }
    }

    public bool BeginEdit()
    {
        if (_terminal || !CanInteract || IsReadOnly || _editing)
            return false;
        _editOriginal = _linearColor;
        _editing = true;
        EditingStarted?.Invoke(this);
        return !_terminal && _editing;
    }

    /// <summary>Opens a bounded native popup with presets and arbitrary sRGB hex/RGBA editing.</summary>
    public bool OpenPalette()
    {
        if (_terminal || IsDestroyed || IsDestroyQueued)
            return false;
        if (_popup is null)
            return false;
        if (_popup.IsOpen)
            return true;
        if (!BeginEdit())
            return false;
        _popup.PreferredWidth = 228;
        _popup.ShowContent(PopupPosition, 9 * 8 + 6 * 26, BuildColorPopup);
        return true;
    }

    private void BuildColorPopup(SceneNode list)
    {
        for (int rowIndex = 0; rowIndex < Palette.Length; rowIndex += 3)
        {
            SceneNode row = new(list) { Name = "Color presets" };
            UIListTransform rowTransform = row.SetTransform<UIListTransform>();
            rowTransform.DisplayHorizontal = true;
            rowTransform.ItemSize = 76;
            rowTransform.Height = 24;
            rowTransform.MinAnchor = Vector2.Zero;
            rowTransform.MaxAnchor = new Vector2(1, 0);
            for (int index = rowIndex; index < Math.Min(rowIndex + 3, Palette.Length); index++)
            {
                (string name, string presetHex) = Palette[index];
                AddPopupButton(row, name, () =>
                {
                    if (PreviewHexSrgb(presetHex))
                    {
                        CommitEdit();
                        _popup?.Hide();
                    }
                });
            }
        }

        UITextInputComponent hex = AddPopupInput(list, "Hex sRGB", ToHexSrgb(_linearColor));
        UIByteInputComponent[] channels = new UIByteInputComponent[4];
        byte[] initial =
        [
            ToByte(ToSrgb(_linearColor.R)), ToByte(ToSrgb(_linearColor.G)),
            ToByte(ToSrgb(_linearColor.B)), ToByte(_linearColor.A)
        ];
        string[] names = ["Red", "Green", "Blue", "Alpha"];
        bool hexLastEdited = true;
        for (int i = 0; i < channels.Length; i++)
        {
            UIByteInputComponent field = AddPopupInput<UIByteInputComponent>(
                list, $"{names[i]} 0–255", initial[i].ToString(CultureInfo.InvariantCulture));
            channels[i] = field;
            field.GotFocus += _ => hexLastEdited = false;
            field.Submitted += _ => PreviewChannels();
            field.LostFocus += _ => PreviewChannels();
        }
        hex.GotFocus += _ => hexLastEdited = true;
        hex.Submitted += _ => PreviewHex();
        hex.LostFocus += _ => PreviewHex();
        hex.Cancelled += _ => _popup?.Hide();

        bool PreviewHex()
        {
            if (!PreviewHexSrgb(hex.Text))
            {
                hex.IsInvalid = true;
                return false;
            }
            hex.IsInvalid = false;
            byte[] bytes =
            [
                ToByte(ToSrgb(_linearColor.R)), ToByte(ToSrgb(_linearColor.G)),
                ToByte(ToSrgb(_linearColor.B)), ToByte(_linearColor.A)
            ];
            for (int i = 0; i < channels.Length; i++)
                channels[i].Text = bytes[i].ToString(CultureInfo.InvariantCulture);
            return true;
        }

        bool PreviewChannels()
        {
            byte[] values = new byte[4];
            for (int i = 0; i < values.Length; i++)
            {
                if (!byte.TryParse(channels[i].Text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out values[i]))
                {
                    channels[i].IsInvalid = true;
                    return false;
                }
                channels[i].IsInvalid = false;
            }
            if (!PreviewColor(new ColorF4(values[0] / 255f, values[1] / 255f,
                values[2] / 255f, values[3] / 255f), EUIColorSpace.SRgb))
                return false;
            hex.Text = ToHexSrgb(_linearColor);
            return true;
        }

        SceneNode actions = new(list) { Name = "Color actions" };
        UIListTransform actionsTransform = actions.SetTransform<UIListTransform>();
        actionsTransform.DisplayHorizontal = true;
        actionsTransform.ItemSize = 114;
        actionsTransform.Height = 24;
        actionsTransform.MinAnchor = Vector2.Zero;
        actionsTransform.MaxAnchor = new Vector2(1, 0);
        AddPopupButton(actions, "Apply", () =>
        {
            if (!(hexLastEdited ? PreviewHex() : PreviewChannels()))
                return;
            CommitEdit();
            _popup?.Hide();
        });
        AddPopupButton(actions, "Cancel", () => _popup?.Hide());
    }

    private static T AddPopupInput<T>(SceneNode parent, string label, string value)
        where T : UITextInputComponent, new()
    {
        SceneNode row = new(parent) { Name = label };
        UIBoundableTransform bounds = row.SetTransform<UIBoundableTransform>();
        bounds.MinAnchor = Vector2.Zero;
        bounds.MaxAnchor = new Vector2(1, 0);
        bounds.Height = 26;
        row.AddComponent<UIMaterialComponent>()!.SetBatchSolidColor(new ColorF4(0.18f, 0.18f, 0.18f, 1));
        UITextComponent text = row.AddComponent<UITextComponent>()!;
        text.FontSize = 12;
        text.Color = ColorF4.White;
        text.VerticalAlignment = EVerticalAlignment.Center;
        T input = row.AddComponent<T>()!;
        input.AccessibilityLabel = label;
        input.SingleLineMode = true;
        input.CommitOnSubmit = true;
        input.Text = value;
        return input;
    }

    private static UITextInputComponent AddPopupInput(SceneNode parent, string label, string value)
        => AddPopupInput<UITextInputComponent>(parent, label, value);

    private static void AddPopupButton(SceneNode parent, string label, Action action)
    {
        SceneNode row = parent.NewChild<UIButtonComponent, UIMaterialComponent>(
            out UIButtonComponent button, out UIMaterialComponent background);
        UIBoundableTransform bounds = row.GetTransformAs<UIBoundableTransform>(true)!;
        bounds.MinAnchor = Vector2.Zero;
        bounds.MaxAnchor = new Vector2(1, 0);
        bounds.Height = 24;
        background.SetBatchSolidColor(ColorF4.Transparent);
        button.AccessibilityLabel = label;
        button.RegisterClickActions(_ => action());
        row.NewChild<UITextComponent>(out UITextComponent text);
        text.Text = label;
        text.FontSize = 12;
        text.Color = ColorF4.White;
        text.HorizontalAlignment = EHorizontalAlignment.Center;
        text.VerticalAlignment = EVerticalAlignment.Center;
    }

    private void OnPopupDismissed(UIContextMenuComponent source, long revision)
    {
        if (_ownerActive && OwnsPopupRevision(revision) && ReferenceEquals(_subscribedPopup, source))
            CancelEdit();
    }

    public bool PreviewColor(ColorF4 color, EUIColorSpace colorSpace)
    {
        if (_terminal || !_editing || !CanInteract || IsReadOnly)
            return false;
        ColorF4 linear = colorSpace == EUIColorSpace.SRgb ? SrgbToLinear(color) : color;
        ColorF4 previous = _linearColor;
        ApplyLinear(linear);
        if (_terminal || IsDestroyed || IsDestroyQueued)
            return false;
        long revision = _bindingRevision;
        ColorPreviewed?.Invoke(new(previous, linear, EUIValueChangeOrigin.User));
        return _editing && OwnsBindingRevision(revision);
    }

    public bool PreviewHexSrgb(ReadOnlySpan<char> hex)
    {
        if (hex.Length > 0 && hex[0] == '#')
            hex = hex[1..];
        if (hex.Length is not (6 or 8) ||
            !byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return false;
        byte a = 255;
        if (hex.Length == 8 && !byte.TryParse(hex.Slice(6, 2), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out a))
            return false;
        return PreviewColor(new ColorF4(r / 255f, g / 255f, b / 255f, a / 255f), EUIColorSpace.SRgb);
    }

    public void CommitEdit()
    {
        if (_terminal || !_editing)
            return;
        _editing = false;
        IUIValueBinding<ColorF4>? binding = _binding;
        if (binding is not null)
        {
            long revision = _bindingRevision;
            binding.SetValue(_linearColor);
            if (!OwnsBindingRevision(revision) || !ReferenceEquals(_binding, binding) ||
                !IsActiveInHierarchy)
                return;
            ApplyLinear(binding.Value);
        }
        if (!_terminal && !_editOriginal.Equals(_linearColor))
            ColorCommitted?.Invoke(new(_editOriginal, _linearColor, EUIValueChangeOrigin.User));
    }

    public void CancelEdit()
    {
        if (_terminal || !_editing)
            return;
        _editing = false;
        ApplyLinear(_binding?.Value ?? _editOriginal);
    }

    public override void CancelForRecycle()
    {
        _popup?.Hide();
        CancelEdit();
        Binding = null;
        Popup = null;
        base.CancelForRecycle();
    }

    private static bool IsFinite(ColorF4 color)
        => float.IsFinite(color.R) && float.IsFinite(color.G) &&
           float.IsFinite(color.B) && float.IsFinite(color.A);

    public static ColorF4 SrgbToLinear(ColorF4 color)
        => new(ToLinear(color.R), ToLinear(color.G), ToLinear(color.B), color.A);

    public static ColorF4 LinearToSrgb(ColorF4 color)
        => new(ToSrgb(color.R), ToSrgb(color.G), ToSrgb(color.B), color.A);

    private static float ToLinear(float value)
        => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    private static float ToSrgb(float value)
        => value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;

    private static string ToHexSrgb(ColorF4 linear)
        => $"#{ToByte(ToSrgb(linear.R)):X2}{ToByte(ToSrgb(linear.G)):X2}" +
           $"{ToByte(ToSrgb(linear.B)):X2}{ToByte(linear.A):X2}";

    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}

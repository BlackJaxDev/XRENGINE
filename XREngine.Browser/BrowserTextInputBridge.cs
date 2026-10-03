using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Browser;

/// <summary>
/// Keeps a browser text service bound to the engine's focused text widget. Geometry
/// is normalized to the rendered canvas so CSS layout and backing DPR stay separate.
/// </summary>
internal sealed class BrowserTextInputBridge
{
    private const int MaximumTextLength = 16_384;
    private static int _nextGeneration;
    private UITextInputComponent? _target;
    private string? _observedValue;
    private string _observedLabel = "Engine text input";
    private int _generation;
    private int _contentVersion;
    private int _labelVersion;
    private XRViewport? _viewport;

    public int Generation => _generation;
    public int ContentVersion
    {
        get
        {
            ObserveContent();
            return _contentVersion;
        }
    }
    public string Value => _target?.Text ?? string.Empty;
    public string Label => _observedLabel;
    public int LabelVersion => _labelVersion;
    public int Cursor => _target?.CursorPosition ?? 0;
    public bool SingleLine => _target?.SingleLineMode ?? true;
    public bool ReadOnly => _target?.RegisterInputsOnFocus == false;
    public float X { get; private set; } = -1;
    public float Y { get; private set; } = -1;
    public float Width { get; private set; }
    public float Height { get; private set; }

    public int Refresh(object? focusedInteractable, XRViewport? viewport, bool hasActiveOwner)
    {
        _viewport = viewport;
        UITextInputComponent? target = focusedInteractable as UITextInputComponent;
        if (!hasActiveOwner || target is not { IsActiveInHierarchy: true, IsFocused: true } ||
            target.AccessibilityHidden ||
            target.UserInterfaceCanvas is not { IsActiveInHierarchy: true } ||
            !target.UITransform.IsVisibleInHierarchy ||
            !BrowserUiBoundsProjection.TryProjectVisible(target, viewport,
                out _, out _, out _, out _))
            target = null;
        if (!ReferenceEquals(target, _target))
        {
            _target = target;
            _generation = target is null ? 0 : Interlocked.Increment(ref _nextGeneration);
            _observedValue = null;
        }

        ObserveContent();
        ObserveLabel();

        UpdateGeometry(viewport);
        return _generation;
    }

    public void Clear()
    {
        _target = null;
        _observedValue = null;
        _observedLabel = "Engine text input";
        _generation = 0;
        _viewport = null;
        X = Y = -1;
        Width = Height = 0;
    }

    public bool Edit(int generation, int expectedVersion, object? focusedInteractable, bool hasActiveOwner,
        string value, int selectionStart, int selectionEnd)
    {
        if (!Owns(generation, focusedInteractable, hasActiveOwner) || ReadOnly || value is null || value.Length > MaximumTextLength)
            return false;
        ObserveContent();
        if (expectedVersion != _contentVersion)
            return false;
        bool accepted = _target!.UserReplaceText(value, selectionStart, selectionEnd);
        ObserveContent();
        return accepted;
    }

    public bool Select(int generation, int expectedVersion, object? focusedInteractable, bool hasActiveOwner, int cursor)
    {
        if (!Owns(generation, focusedInteractable, hasActiveOwner))
            return false;
        ObserveContent();
        if (expectedVersion != _contentVersion || cursor < 0 || cursor > _target!.Text.Length)
            return false;
        _target.CursorPosition = cursor;
        return true;
    }

    public bool Action(int generation, int expectedVersion, object? focusedInteractable, bool hasActiveOwner, bool submit)
    {
        if (!Owns(generation, focusedInteractable, hasActiveOwner) || ReadOnly || !_target!.SingleLineMode)
            return false;
        ObserveContent();
        if (expectedVersion != _contentVersion)
            return false;
        if (submit)
            _target.UserSubmit();
        else
            _target.UserCancel();
        ObserveContent();
        return true;
    }

    private void ObserveContent()
    {
        string? value = _target?.Text;
        if (value is not null && !string.Equals(value, _observedValue, StringComparison.Ordinal))
        {
            _observedValue = value;
            _contentVersion++;
        }
    }

    private void ObserveLabel()
    {
        string label = _target?.AccessibilityName ?? "Engine text input";
        if (!string.Equals(label, _observedLabel, StringComparison.Ordinal))
        {
            _observedLabel = label;
            _labelVersion++;
        }
    }

    private bool Owns(int generation, object? focusedInteractable, bool hasActiveOwner)
        => hasActiveOwner && generation != 0 && generation == _generation &&
           _target is { IsActiveInHierarchy: true, IsFocused: true } &&
           !_target.AccessibilityHidden &&
           _target.UserInterfaceCanvas is { IsActiveInHierarchy: true } &&
           _target.UITransform.IsVisibleInHierarchy &&
           BrowserUiBoundsProjection.TryProjectVisible(_target, _viewport, out _, out _, out _, out _) &&
           ReferenceEquals(_target, focusedInteractable);

    private void UpdateGeometry(XRViewport? viewport)
    {
        UITextInputComponent? target = _target;
        if (target is null || !BrowserUiBoundsProjection.TryProjectVisible(target, viewport,
            out float x, out float y, out float width, out float height))
        {
            X = Y = -1;
            Width = Height = 0;
            return;
        }
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}

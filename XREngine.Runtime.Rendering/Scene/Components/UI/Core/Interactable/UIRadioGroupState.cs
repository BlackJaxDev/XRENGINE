namespace XREngine.Rendering.UI;

/// <summary>Coordinates one selected option among retained radio buttons.</summary>
public sealed class UIRadioGroupState : IDisposable
{
    private readonly List<UIRadioButtonComponent> _buttons = [];
    private ulong? _selectedId;
    private IUIValueBinding<ulong?>? _binding;
    private bool _writingBinding;
    private bool _disposed;

    public ulong? SelectedId => _selectedId;
    public event Action<UIValueChange<ulong?>>? SelectionChanged;

    /// <summary>Sets the selected item source. Set this to null when a group is retired.</summary>
    public IUIValueBinding<ulong?>? Binding
    {
        get => _binding;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_binding, value))
                return;
            if (_binding is not null)
                _binding.ValueChanged -= OnBindingChanged;
            _binding = value;
            if (_binding is not null)
            {
                _binding.ValueChanged += OnBindingChanged;
                SetSelected(_binding.Value, EUIValueChangeOrigin.Binding);
            }
        }
    }

    internal void Register(UIRadioButtonComponent button)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (button.OptionId == 0)
            return;
        for (int i = 0; i < _buttons.Count; i++)
            if (_buttons[i].OptionId == button.OptionId && !ReferenceEquals(_buttons[i], button))
                throw new InvalidOperationException("Radio option IDs must be unique within a group.");
        if (!_buttons.Contains(button))
            _buttons.Add(button);
        button.IsSelected = _selectedId == button.OptionId;
    }

    internal void Unregister(UIRadioButtonComponent button)
    {
        if (!_buttons.Remove(button))
            return;
        button.IsSelected = false;
    }

    /// <summary>Clears a removed logical option while preserving selection on row recycle.</summary>
    public void RemoveOption(ulong optionId)
    {
        if (_selectedId == optionId)
            SetSelected(null, EUIValueChangeOrigin.Programmatic);
    }

    public bool Select(ulong optionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < _buttons.Count; i++)
            if (_buttons[i].OptionId == optionId && _buttons[i].CanInteract && !_buttons[i].IsReadOnly)
            {
                SetSelected(optionId, EUIValueChangeOrigin.User);
                return true;
            }
        return false;
    }

    public bool SelectAdjacent(UIRadioButtonComponent current, int direction)
    {
        int index = _buttons.IndexOf(current);
        if (index < 0 || direction == 0)
            return false;
        IUICanvasInputSource? input = current.UserInterfaceCanvas?.GetInputComponent();
        for (int offset = 1; offset < _buttons.Count; offset++)
        {
            int candidate = (index + (direction > 0 ? offset : _buttons.Count - offset)) % _buttons.Count;
            UIRadioButtonComponent button = _buttons[candidate];
            if (!button.CanInteract || button.IsReadOnly ||
                input is not null && !input.TryFocusPresented(button))
                continue;
            if (Select(button.OptionId) && SelectedId == button.OptionId)
                return true;
            if (input is not null)
                input.TryFocusPresented(current);
        }
        return false;
    }

    private void OnBindingChanged(ulong? id)
    {
        if (!_writingBinding)
            SetSelected(id, EUIValueChangeOrigin.Binding);
    }

    private void SetSelected(ulong? id, EUIValueChangeOrigin origin)
    {
        if (_disposed)
            return;
        IUIValueBinding<ulong?>? binding = _binding;
        if (binding is not null && origin != EUIValueChangeOrigin.Binding)
        {
            _writingBinding = true;
            try { binding.SetValue(id); }
            finally { _writingBinding = false; }
            if (_disposed || !ReferenceEquals(_binding, binding))
                return;
            id = binding.Value;
        }
        if (_selectedId == id)
            return;
        ulong? previous = _selectedId;
        _selectedId = id;
        UIRadioButtonComponent[] buttons = [.. _buttons];
        for (int i = 0; i < buttons.Length; i++)
            if (ReferenceEquals(buttons[i].Group, this) && _buttons.Contains(buttons[i]))
                buttons[i].IsSelected = buttons[i].OptionId == id;
        SelectionChanged?.Invoke(new(previous, id, origin));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_binding is not null)
            _binding.ValueChanged -= OnBindingChanged;
        _binding = null;
        UIRadioButtonComponent[] buttons = [.. _buttons];
        _buttons.Clear();
        for (int i = 0; i < buttons.Length; i++)
            buttons[i].IsSelected = false;
        SelectionChanged = null;
    }
}

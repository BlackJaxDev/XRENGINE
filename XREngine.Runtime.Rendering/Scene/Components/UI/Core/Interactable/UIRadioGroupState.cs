namespace XREngine.Rendering.UI;

/// <summary>Coordinates one selected option among retained radio buttons.</summary>
public sealed class UIRadioGroupState : IDisposable
{
    private readonly List<UIRadioButtonComponent> _buttons = [];
    private readonly List<UIRadioButtonComponent> _owners = [];
    private ulong? _selectedId;
    private uint _selectionRevision;
    private IUIValueBinding<ulong?>? _binding;
    private Action<ulong?>? _bindingHandler;
    private uint _bindingRevision;
    private uint _writingBindingRevision;
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
            IUIValueBinding<ulong?>? previous = _binding;
            Action<ulong?>? previousHandler = _bindingHandler;
            _binding = value;
            _bindingHandler = null;
            uint revision = ++_bindingRevision;
            Exception? failure = null;
            if (previous is not null && previousHandler is not null)
                try { previous.ValueChanged -= previousHandler; }
                catch (Exception error) { failure = error; }
            if (!_disposed && revision == _bindingRevision && ReferenceEquals(_binding, value) && value is not null)
            {
                Action<ulong?> handler = id => OnBindingChanged(value, revision, id);
                _bindingHandler = handler;
                try
                {
                    value.ValueChanged += handler;
                    if (!_disposed && revision == _bindingRevision && ReferenceEquals(_binding, value))
                    {
                        ulong? selected = value.Value;
                        if (!_disposed && revision == _bindingRevision && ReferenceEquals(_binding, value))
                            SetSelected(selected, EUIValueChangeOrigin.Binding);
                    }
                    if (_disposed || revision != _bindingRevision || !ReferenceEquals(_binding, value))
                        value.ValueChanged -= handler;
                }
                catch (Exception error)
                {
                    failure = Combine(failure, error);
                    if (!_disposed && revision == _bindingRevision && ReferenceEquals(_binding, value))
                    {
                        _binding = null;
                        _bindingHandler = null;
                        ++_bindingRevision;
                    }
                    try { value.ValueChanged -= handler; }
                    catch (Exception detachError) { failure = Combine(failure, detachError); }
                }
            }
            if (failure is not null)
                throw failure;
        }
    }

    internal void Register(UIRadioButtonComponent button)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(button.Group, this) || button.IsDestroyed || button.IsDestroyQueued)
            return;
        AttachOwner(button);
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

    internal void AttachOwner(UIRadioButtonComponent button)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_owners.Contains(button))
            _owners.Add(button);
    }

    internal void DetachOwner(UIRadioButtonComponent button)
    {
        _owners.Remove(button);
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
            if (ReferenceEquals(_buttons[i].Group, this) && _buttons[i].OptionId == optionId &&
                _buttons[i].CanInteract && !_buttons[i].IsReadOnly)
            {
                SetSelected(optionId, EUIValueChangeOrigin.User);
                return true;
            }
        return false;
    }

    public bool SelectAdjacent(UIRadioButtonComponent current, int direction)
    {
        if (_disposed || !ReferenceEquals(current.Group, this))
            return false;
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

    private void OnBindingChanged(IUIValueBinding<ulong?> source, uint revision, ulong? id)
    {
        if (!_disposed && revision == _bindingRevision && ReferenceEquals(_binding, source) &&
            _writingBindingRevision != revision)
            SetSelected(id, EUIValueChangeOrigin.Binding);
    }

    private void SetSelected(ulong? id, EUIValueChangeOrigin origin)
    {
        if (_disposed)
            return;
        IUIValueBinding<ulong?>? binding = _binding;
        if (binding is not null && origin != EUIValueChangeOrigin.Binding)
        {
            uint revision = _bindingRevision;
            _writingBindingRevision = revision;
            try { binding.SetValue(id); }
            finally
            {
                if (_writingBindingRevision == revision)
                    _writingBindingRevision = 0;
            }
            if (_disposed || revision != _bindingRevision || !ReferenceEquals(_binding, binding))
                return;
            id = binding.Value;
        }
        if (_selectedId == id)
            return;
        ulong? previous = _selectedId;
        _selectedId = id;
        uint selectionRevision = ++_selectionRevision;
        UIRadioButtonComponent[] buttons = [.. _buttons];
        Exception? failure = null;
        for (int i = 0; i < buttons.Length; i++)
            if (!_disposed && selectionRevision == _selectionRevision &&
                ReferenceEquals(buttons[i].Group, this) && _buttons.Contains(buttons[i]))
                try { buttons[i].IsSelected = buttons[i].OptionId == id; }
                catch (Exception error) { failure = Combine(failure, error); }
        try { if (!_disposed && selectionRevision == _selectionRevision) SelectionChanged?.Invoke(new(previous, id, origin)); }
        catch (Exception error) { failure = Combine(failure, error); }
        if (failure is not null)
            throw failure;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ++_bindingRevision;
        IUIValueBinding<ulong?>? binding = _binding;
        Action<ulong?>? handler = _bindingHandler;
        _binding = null;
        _bindingHandler = null;
        SelectionChanged = null;
        UIRadioButtonComponent[] buttons = [.. _owners];
        _owners.Clear();
        _buttons.Clear();
        Exception? failure = null;
        if (binding is not null && handler is not null)
            try { binding.ValueChanged -= handler; }
            catch (Exception error) { failure = error; }
        for (int i = 0; i < buttons.Length; i++)
        {
            bool owned = buttons[i].DetachDisposedGroup(this);
            if (!owned)
                continue;
            try { buttons[i].IsSelected = false; }
            catch (Exception error) { failure = Combine(failure, error); }
        }
        if (failure is not null)
            throw failure;
    }

    private static Exception Combine(Exception? first, Exception second)
        => first is null ? second : new AggregateException(first, second);
}

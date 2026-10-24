using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using XREngine.Input.Devices;
using XREngine.Input;
using XREngine.Rendering.UI.State;
using XREngine.Data.Colors;
using XREngine.Scene;

namespace XREngine.Rendering.UI
{
    public delegate void DelScrolling(bool up);
    public class UIComboBoxComponent : UIInteractableComponent
    {
        private const int MaximumPopupOptions = 256;
        private UIComboOption[] _options = [];
        private IUIComboOptionSource? _virtualOptions;
        private VirtualOptionAdapter? _virtualAdapter;
        private ulong? _selectedId;
        private UIContextMenuComponent? _popup;
        private IUIValueBinding<ulong?>? _binding;
        private bool _writingBinding;
        private bool _restoreFocusOnDismiss;
        private bool _tearingDown;
        private bool _opening;
        private ulong _popupRevision;
        private ulong _optionsRevision;
        private ulong _bindingRevision;
        private ulong _selectionRevision;
        private string _typeAhead = string.Empty;
        private long _lastTypeAheadTick;
        /// <summary>Limits the number of characters in a combo search prefix.</summary>
        public const int MaximumTypeAheadLength = 64;
        private const double TypeAheadTimeoutSeconds = 1;

        public UIComboBoxComponent()
        {
            InteractAction += _ => Open();
            BackAction += _ => Close();
        }

        public override EUIAccessibilityRole AccessibilityRole => EUIAccessibilityRole.ComboBox;
        public override bool TryConsumeFocusDirection(Vector2 direction)
        {
            if (!CanInteract || IsReadOnly || !float.IsFinite(direction.Y) ||
                MathF.Abs(direction.Y) <= MathF.Abs(direction.X))
                return false;
            return SelectAdjacent(direction.Y > 0 ? 1 : -1);
        }
        public override bool AccessibilityActivate() => Open();
        public override bool AccessibilityAdjust(int direction)
        {
            if (direction == 0 || !CanInteract || IsReadOnly)
                return false;
            return SelectAdjacent(Math.Sign(direction));
        }
        public override string? AccessibilityValue
        {
            get
            {
                if (_virtualOptions is not null && _selectedId is { } virtualId)
                {
                    int index = _virtualOptions.IndexOfId(virtualId);
                    return index >= 0 ? _virtualOptions.GetOption(index).Label : null;
                }
                for (int i = 0; i < _options.Length; i++)
                    if (_options[i].Id == _selectedId)
                        return _options[i].Label;
                return null;
            }
        }

        /// <summary>Popup node owned and positioned by the control host.</summary>
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
                Close();
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

        public Vector2 PopupPosition { get; set; }
        public ulong? SelectedId => _selectedId;
        public bool SupportsTypeAhead => _virtualOptions is null ||
            _virtualOptions.Count <= MaximumPopupOptions || _virtualOptions is IUIComboPrefixSearch;
        public bool SupportsAdjacentSelection => _virtualOptions is null ||
            _virtualOptions.Count <= MaximumPopupOptions || _virtualOptions is IUIComboEnabledNavigation;
        public EUIComboLookupStatus LastTypeAheadStatus { get; private set; }
        public EUIComboLookupStatus LastNavigationStatus { get; private set; }
        /// <summary>Allows the host to place the popup before opening it.</summary>
        public event Action<UIComboBoxComponent>? Opening;
        public event Action<UIValueChange<ulong?>>? SelectionChanged;

        /// <summary>Uses a virtual popup for a large source with stable IDs.</summary>
        public IUIComboOptionSource? VirtualOptions
        {
            get => _virtualOptions;
            set
            {
                if (_tearingDown || IsDestroyed)
                    return;
                if (ReferenceEquals(_virtualOptions, value))
                    return;
                ulong revision = ++_optionsRevision;
                Close();
                if (!OwnsOptionsChange(revision))
                    return;
                SetField(ref _virtualOptions, value);
                if (!OwnsOptionsChange(revision) || !ReferenceEquals(_virtualOptions, value))
                    return;
                LastTypeAheadStatus = EUIComboLookupStatus.None;
                LastNavigationStatus = EUIComboLookupStatus.None;
                _virtualAdapter = value is null ? null : new VirtualOptionAdapter(this, value);
                if (_selectedId is { } id && !HasOption(id) && OwnsOptionsChange(revision))
                    Select(null, EUIValueChangeOrigin.Programmatic);
            }
        }

        public IUIValueBinding<ulong?>? Binding
        {
            get => _binding;
            set
            {
                if (_tearingDown || IsDestroyed)
                    return;
                if (ReferenceEquals(_binding, value))
                    return;
                ulong revision = ++_bindingRevision;
                IUIValueBinding<ulong?>? previous = _binding;
                if (IsActiveInHierarchy && previous is not null)
                    previous.ValueChanged -= OnBindingChanged;
                if (!OwnsBindingChange(revision))
                    return;
                bool changed = SetField(ref _binding, value);
                if (!changed && OwnsBindingChange(revision) &&
                    ReferenceEquals(_binding, previous) && IsActiveInHierarchy && previous is not null)
                    previous.ValueChanged += OnBindingChanged;
                if (!OwnsBindingChange(revision) || !ReferenceEquals(_binding, value))
                    return;
                if (IsActiveInHierarchy && _binding is not null)
                {
                    _binding.ValueChanged += OnBindingChanged;
                    if (!OwnsBindingChange(revision) || !ReferenceEquals(_binding, value))
                    {
                        if (value is not null)
                            value.ValueChanged -= OnBindingChanged;
                        return;
                    }
                }
                if (value is not null && OwnsBindingChange(revision))
                    Select(value.Value, EUIValueChangeOrigin.Binding);
            }
        }

        public void SetOptions(ReadOnlySpan<UIComboOption> options)
        {
            if (_tearingDown || IsDestroyed)
                return;
            if (options.Length > MaximumPopupOptions)
                throw new NotSupportedException("Large combo sources require a virtual popup provider.");
            for (int i = 0; i < options.Length; i++)
            {
                if (options[i].Id == 0)
                    throw new ArgumentException("Combo option IDs must be nonzero.", nameof(options));
                for (int j = 0; j < i; j++)
                    if (options[i].Id == options[j].Id)
                        throw new ArgumentException("Combo option IDs must be unique.", nameof(options));
            }
            _options = options.ToArray();
            VirtualOptions = null;
            if (_tearingDown || IsDestroyed)
                return;
            LastTypeAheadStatus = EUIComboLookupStatus.None;
            LastNavigationStatus = EUIComboLookupStatus.None;
            if (_selectedId is { } id && !HasOption(id))
                Select(null, EUIValueChangeOrigin.Programmatic);
            if (_popup?.IsOpen == true)
                Open();
        }

        private bool HasOption(ulong id)
        {
            if (_virtualOptions is not null)
                return _virtualOptions.IndexOfId(id) >= 0;
            for (int i = 0; i < _options.Length; i++)
                if (_options[i].Id == id)
                    return true;
            return false;
        }

        public bool Select(ulong? id, EUIValueChangeOrigin origin = EUIValueChangeOrigin.Programmatic)
        {
            if (_tearingDown || IsDestroyed)
                return false;
            ulong selectionRevision = ++_selectionRevision;
            IUIComboOptionSource? source = _virtualOptions;
            UIComboOption[] options = _options;
            ulong optionsRevision = _optionsRevision;
            IUIValueBinding<ulong?>? binding = _binding;
            ulong bindingRevision = _bindingRevision;
            ulong sourceRevision = source?.Revision ?? 0;
            if (!OwnsSelectionIntent(selectionRevision, source, sourceRevision, options,
                optionsRevision, binding, bindingRevision))
                return false;
            if (id is { } selected && !HasOption(selected))
                return false;
            if (!OwnsSelectionIntent(selectionRevision, source, sourceRevision, options,
                optionsRevision, binding, bindingRevision))
                return false;
            if (binding is not null && origin != EUIValueChangeOrigin.Binding)
            {
                bool wasWritingBinding = _writingBinding;
                _writingBinding = true;
                try { binding.SetValue(id); }
                finally { _writingBinding = wasWritingBinding; }
                if (!OwnsSelectionIntent(selectionRevision, source, sourceRevision, options,
                    optionsRevision, binding, bindingRevision))
                    return false;
                id = binding.Value;
                if (!OwnsSelectionIntent(selectionRevision, source, sourceRevision, options,
                    optionsRevision, binding, bindingRevision))
                    return false;
                if (id is { } boundId && !HasOption(boundId))
                    return false;
                if (!OwnsSelectionIntent(selectionRevision, source, sourceRevision, options,
                    optionsRevision, binding, bindingRevision))
                    return false;
            }
            if (_selectedId == id)
                return true;
            ulong? previous = _selectedId;
            if (!SetField(ref _selectedId, id, nameof(SelectedId)) ||
                !OwnsSelectionIntent(selectionRevision, source, sourceRevision, options,
                    optionsRevision, binding, bindingRevision) || _selectedId != id)
                return false;
            SelectionChanged?.Invoke(new(previous, id, origin));
            return OwnsSelectionIntent(selectionRevision, source, sourceRevision, options,
                optionsRevision, binding, bindingRevision) && _selectedId == id;
        }

        private bool OwnsSelectionIntent(ulong selectionRevision, IUIComboOptionSource? source,
            ulong sourceRevision, UIComboOption[] options, ulong optionsRevision,
            IUIValueBinding<ulong?>? binding, ulong bindingRevision)
        {
            if (_tearingDown || IsDestroyed || selectionRevision != _selectionRevision ||
                optionsRevision != _optionsRevision || bindingRevision != _bindingRevision ||
                !ReferenceEquals(_virtualOptions, source) || !ReferenceEquals(_options, options) ||
                !ReferenceEquals(_binding, binding) ||
                source is not null && source.Revision != sourceRevision)
                return false;
            return !_tearingDown && !IsDestroyed && selectionRevision == _selectionRevision &&
                optionsRevision == _optionsRevision && bindingRevision == _bindingRevision &&
                ReferenceEquals(_virtualOptions, source) && ReferenceEquals(_options, options) &&
                ReferenceEquals(_binding, binding);
        }

        private void OnBindingChanged(ulong? id)
        {
            if (!_tearingDown && !IsDestroyed && !_writingBinding)
                Select(id, EUIValueChangeOrigin.Binding);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T value)
        {
            if (_tearingDown || IsDestroyed)
                return false;
            ulong popupRevision = _popupRevision;
            ulong optionsRevision = _optionsRevision;
            ulong bindingRevision = _bindingRevision;
            ulong selectionRevision = _selectionRevision;
            IUIComboOptionSource? selectionSource = propName == nameof(SelectedId) ? _virtualOptions : null;
            UIComboOption[]? selectionOptions = propName == nameof(SelectedId) ? _options : null;
            IUIValueBinding<ulong?>? selectionBinding = propName == nameof(SelectedId) ? _binding : null;
            ulong sourceRevision = selectionSource?.Revision ?? 0;
            if (_tearingDown || IsDestroyed ||
                propName == nameof(SelectedId) && selectionRevision != _selectionRevision)
                return false;
            bool allowed = base.OnPropertyChanging(propName, field, value);
            return allowed && !_tearingDown && !IsDestroyed && (propName switch
            {
                nameof(Popup) => popupRevision == _popupRevision,
                nameof(VirtualOptions) => optionsRevision == _optionsRevision,
                nameof(Binding) => bindingRevision == _bindingRevision,
                nameof(SelectedId) => OwnsSelectionIntent(selectionRevision, selectionSource,
                    sourceRevision, selectionOptions!, optionsRevision, selectionBinding, bindingRevision),
                _ => true
            });
        }

        private bool OwnsPopupChange(ulong revision)
            => !_tearingDown && !IsDestroyed && revision == _popupRevision;

        private bool OwnsOptionsChange(ulong revision)
            => !_tearingDown && !IsDestroyed && revision == _optionsRevision;

        private bool OwnsBindingChange(ulong revision)
            => !_tearingDown && !IsDestroyed && revision == _bindingRevision;

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(SelectedId) && SceneNode is not null && !SceneNode.IsTransformNull)
                UITransform.RecordUIChange(UIChangeDomain.Visual);
        }

        public bool Open()
        {
            if (_tearingDown || IsDestroyed || _opening || !CanInteract || IsReadOnly ||
                _popup is not { IsDestroyed: false } popup)
                return false;
            VirtualOptionAdapter? adapter = _virtualAdapter;
            UIComboOption[] options = _options;
            if (adapter is not null)
            {
                if (adapter.Count == 0)
                    return false;
                if (_tearingDown || IsDestroyed || !ReferenceEquals(_virtualAdapter, adapter))
                    return false;
                if (!InvokeOpening(popup, adapter, options))
                    return false;
                popup.ShowVirtual(PopupPosition, adapter);
                if (_tearingDown || IsDestroyed || !popup.IsOpen ||
                    !ReferenceEquals(_popup, popup) ||
                    !ReferenceEquals(_virtualAdapter, adapter))
                    return false;
                adapter.List = popup.VirtualList;
                _restoreFocusOnDismiss = true;
                return true;
            }
            if (options.Length == 0)
                return false;
            if (!InvokeOpening(popup, adapter, options))
                return false;
            ContextMenuItem[] items = new ContextMenuItem[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                UIComboOption option = options[i];
                items[i] = new ContextMenuItem(option.Label,
                    () => Select(option.Id, EUIValueChangeOrigin.User), option.IsEnabled);
            }
            popup.Show(PopupPosition, items);
            if (_tearingDown || IsDestroyed || !popup.IsOpen ||
                !ReferenceEquals(_popup, popup))
                return false;
            _restoreFocusOnDismiss = true;
            return true;
        }

        private bool InvokeOpening(UIContextMenuComponent popup, VirtualOptionAdapter? adapter,
            UIComboOption[] options)
        {
            _opening = true;
            try { Opening?.Invoke(this); }
            finally { _opening = false; }
            return !_tearingDown && !IsDestroyed && CanInteract && !IsReadOnly &&
                !popup.IsDestroyed && ReferenceEquals(_popup, popup) &&
                ReferenceEquals(_virtualAdapter, adapter) && ReferenceEquals(_options, options);
        }

        public void Close()
        {
            _restoreFocusOnDismiss = false;
            _typeAhead = string.Empty;
            if (_popup is { IsDestroyed: false } popup)
                popup.Hide();
        }

        private void OnPopupDismissed()
        {
            if (_tearingDown || IsDestroyed)
                return;
            if (_restoreFocusOnDismiss && CanInteract)
                IsFocused = true;
            _restoreFocusOnDismiss = false;
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            if (_binding is not null)
            {
                _binding.ValueChanged += OnBindingChanged;
                Select(_binding.Value, EUIValueChangeOrigin.Binding);
            }
            if (_popup is not null)
                _popup.Dismissed += OnPopupDismissed;
        }

        protected override void OnComponentDeactivated()
        {
            Close();
            if (_popup is not null)
                _popup.Dismissed -= OnPopupDismissed;
            if (_binding is not null)
                _binding.ValueChanged -= OnBindingChanged;
            base.OnComponentDeactivated();
        }

        protected override void OnDestroying()
        {
            _tearingDown = true;
            _restoreFocusOnDismiss = false;
            _typeAhead = string.Empty;
            if (_binding is not null)
                _binding.ValueChanged -= OnBindingChanged;
            UIContextMenuComponent? popup = _popup;
            if (popup is not null)
                popup.Dismissed -= OnPopupDismissed;
            if (_virtualAdapter is not null)
                _virtualAdapter.List = null;
            _virtualAdapter = null;
            _virtualOptions = null;
            _options = [];
            _binding = null;
            _popup = null;
            Opening = null;
            SelectionChanged = null;
            try
            {
                if (popup is { IsDestroyed: false })
                    popup.Hide();
            }
            finally
            {
                base.OnDestroying();
            }
        }

        public override void CancelForRecycle()
        {
            Close();
            Binding = null;
            Popup = null;
            base.CancelForRecycle();
        }

        public override void RegisterInput(IInputRegistration input)
        {
            base.RegisterInput(input);
            input.RegisterKeyCharacter(OnTypeAheadCharacter);
            input.RegisterKeyStateChange(EKey.Down, SelectNext);
            input.RegisterKeyStateChange(EKey.Up, SelectPrevious);
            input.RegisterKeyStateChange(EKey.Escape, Escape);
            input.RegisterKeyStateChange(EKey.Enter, OpenKey);
            input.RegisterKeyStateChange(EKey.Space, OpenKey);
        }

        private void OnTypeAheadCharacter(char character) => TypeAhead(character);

        /// <summary>Selects an enabled option that starts with the entered text.</summary>
        public bool TypeAhead(char character)
        {
            if (!CanInteract || IsReadOnly || char.IsControl(character) || char.IsSurrogate(character))
            {
                LastTypeAheadStatus = EUIComboLookupStatus.NotInteractive;
                return false;
            }
            long now = Stopwatch.GetTimestamp();
            bool expired = _lastTypeAheadTick == 0 ||
                Stopwatch.GetElapsedTime(_lastTypeAheadTick, now).TotalSeconds > TypeAheadTimeoutSeconds;
            _lastTypeAheadTick = now;
            string prefix = expired || _typeAhead.Length >= MaximumTypeAheadLength
                ? character.ToString() : _typeAhead + character;
            EUIComboLookupStatus result = TrySelectPrefix(prefix);
            if (result == EUIComboLookupStatus.Selected)
            {
                _typeAhead = prefix;
                LastTypeAheadStatus = result;
                return true;
            }
            if (result != EUIComboLookupStatus.NoMatch)
            {
                _typeAhead = string.Empty;
                LastTypeAheadStatus = result;
                return false;
            }
            prefix = character.ToString();
            result = TrySelectPrefix(prefix);
            if (result == EUIComboLookupStatus.Selected)
            {
                _typeAhead = prefix;
                LastTypeAheadStatus = result;
                return true;
            }
            _typeAhead = string.Empty;
            LastTypeAheadStatus = result;
            return false;
        }

        private EUIComboLookupStatus TrySelectPrefix(string prefix)
        {
            IUIComboOptionSource? source = _virtualOptions;
            int count = source?.Count ?? _options.Length;
            if (count <= 0)
                return EUIComboLookupStatus.NoMatch;
            ulong revision = source?.Revision ?? 0;
            int selectedIndex = -1;
            if (_selectedId is { } selected)
            {
                if (source is not null)
                    selectedIndex = source.IndexOfId(selected);
                else
                    for (int i = 0; i < _options.Length; i++)
                        if (_options[i].Id == selected)
                        {
                            selectedIndex = i;
                            break;
                        }
            }
            if (source is not null && !SourceUnchanged(source, count, revision))
                return EUIComboLookupStatus.ProviderChanged;
            if (selectedIndex < -1 || selectedIndex >= count)
                return EUIComboLookupStatus.InvalidProviderResult;
            int start = (selectedIndex + 1) % count;
            if (source is IUIComboPrefixSearch search)
            {
                int match = search.FindPrefix(prefix, start);
                return SelectIndexedOption(source, count, revision, match, prefix);
            }
            if (count > MaximumPopupOptions)
                return EUIComboLookupStatus.IndexRequired;
            for (int offset = 0; offset < count; offset++)
            {
                int index = (start + offset) % count;
                UIComboOption option = source?.GetOption(index) ?? _options[index];
                if (source is not null && !SourceUnchanged(source, count, revision))
                    return EUIComboLookupStatus.ProviderChanged;
                if (option.IsEnabled && option.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return Select(option.Id, EUIValueChangeOrigin.User)
                        ? EUIComboLookupStatus.Selected : EUIComboLookupStatus.SelectionRejected;
            }
            return EUIComboLookupStatus.NoMatch;
        }

        private bool SourceUnchanged(IUIComboOptionSource source, int count, ulong revision)
            => ReferenceEquals(_virtualOptions, source) && source.Count == count && source.Revision == revision;

        private EUIComboLookupStatus SelectIndexedOption(IUIComboOptionSource source,
            int count, ulong revision, int index, string? prefix = null)
        {
            if (!SourceUnchanged(source, count, revision))
                return EUIComboLookupStatus.ProviderChanged;
            if (index == -1)
                return EUIComboLookupStatus.NoMatch;
            if (index < 0 || index >= count)
                return EUIComboLookupStatus.InvalidProviderResult;
            UIComboOption option = source.GetOption(index);
            if (!SourceUnchanged(source, count, revision))
                return EUIComboLookupStatus.ProviderChanged;
            if (option.Id == 0 || !option.IsEnabled ||
                (prefix is not null && !option.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                source.IndexOfId(option.Id) != index)
                return EUIComboLookupStatus.InvalidProviderResult;
            if (!SourceUnchanged(source, count, revision))
                return EUIComboLookupStatus.ProviderChanged;
            return Select(option.Id, EUIValueChangeOrigin.User)
                ? EUIComboLookupStatus.Selected : EUIComboLookupStatus.SelectionRejected;
        }

        private void Escape(bool pressed)
        {
            if (pressed)
                Close();
        }

        private void OpenKey(bool pressed)
        {
            if (pressed)
                Open();
        }

        private void SelectNext(bool pressed)
        {
            if (pressed)
                SelectAdjacent(1);
        }

        private void SelectPrevious(bool pressed)
        {
            if (pressed)
                SelectAdjacent(-1);
        }

        private bool SelectAdjacent(int direction)
        {
            if (!CanInteract || IsReadOnly || _virtualOptions is null && _options.Length == 0)
            {
                LastNavigationStatus = EUIComboLookupStatus.NotInteractive;
                return false;
            }
            ulong? previous = SelectedId;
            int index = -1;
            if (_virtualOptions is not null)
            {
                IUIComboOptionSource source = _virtualOptions;
                int count = source.Count;
                ulong revision = source.Revision;
                if (count <= 0)
                {
                    LastNavigationStatus = EUIComboLookupStatus.NoMatch;
                    return false;
                }
                index = _selectedId is { } id ? source.IndexOfId(id) : -1;
                if (!SourceUnchanged(source, count, revision))
                {
                    LastNavigationStatus = EUIComboLookupStatus.ProviderChanged;
                    return false;
                }
                if (index < -1 || index >= count)
                {
                    LastNavigationStatus = EUIComboLookupStatus.InvalidProviderResult;
                    return false;
                }
                if (index < 0 && direction < 0)
                    index = 0;
                if (source is IUIComboEnabledNavigation navigation)
                {
                    int match = navigation.FindNextEnabled(index, direction);
                    LastNavigationStatus = SelectIndexedOption(source, count, revision, match);
                    return LastNavigationStatus == EUIComboLookupStatus.Selected && previous != SelectedId;
                }
                if (count > MaximumPopupOptions)
                {
                    LastNavigationStatus = EUIComboLookupStatus.IndexRequired;
                    return false;
                }
                int candidate = index;
                for (int offset = 1; offset <= count; offset++)
                {
                    candidate = direction > 0
                        ? candidate + 1 == count ? 0 : candidate + 1
                        : candidate <= 0 ? count - 1 : candidate - 1;
                    UIComboOption option = source.GetOption(candidate);
                    if (!SourceUnchanged(source, count, revision))
                    {
                        LastNavigationStatus = EUIComboLookupStatus.ProviderChanged;
                        return false;
                    }
                    if (option.IsEnabled)
                    {
                        LastNavigationStatus = Select(option.Id, EUIValueChangeOrigin.User)
                            ? EUIComboLookupStatus.Selected : EUIComboLookupStatus.SelectionRejected;
                        return LastNavigationStatus == EUIComboLookupStatus.Selected && previous != SelectedId;
                    }
                }
                LastNavigationStatus = EUIComboLookupStatus.NoMatch;
                return false;
            }
            for (int i = 0; i < _options.Length; i++)
                if (_options[i].Id == _selectedId)
                    index = i;
            if (index < 0 && direction < 0)
                index = 0;
            int localCandidate = index;
            for (int offset = 1; offset <= _options.Length; offset++)
            {
                localCandidate = direction > 0
                    ? localCandidate + 1 == _options.Length ? 0 : localCandidate + 1
                    : localCandidate <= 0 ? _options.Length - 1 : localCandidate - 1;
                if (_options[localCandidate].IsEnabled)
                {
                    LastNavigationStatus = Select(_options[localCandidate].Id, EUIValueChangeOrigin.User)
                        ? EUIComboLookupStatus.Selected : EUIComboLookupStatus.SelectionRejected;
                    return LastNavigationStatus == EUIComboLookupStatus.Selected && previous != SelectedId;
                }
            }
            LastNavigationStatus = EUIComboLookupStatus.NoMatch;
            return false;
        }

        private sealed class VirtualOptionAdapter(UIComboBoxComponent owner, IUIComboOptionSource source) : IVirtualListProvider
        {
            public UIListTransform? List { get; set; }
            public int Count => source.Count;
            public ulong Revision => source.Revision;
            public ulong GetItemId(int index) => source.GetOption(index).Id;

            public UIBoundableTransform CreateRow()
            {
                SceneNode node = new("Combo option");
                UIBoundableTransform transform = node.SetTransform<UIBoundableTransform>();
                UIButtonComponent button = node.AddComponent<UIButtonComponent>()!;
                button.DefaultBackgroundColor = ColorF4.DarkGray;
                button.HighlightBackgroundColor = ColorF4.LightGray;
                UIMaterialComponent background = node.AddComponent<UIMaterialComponent>()!;
                background.SetBatchSolidColor(ColorF4.DarkGray);
                node.NewChild<UITextComponent>(out var label);
                label.FontSize = UIContextMenuComponent.FontSize;
                label.Color = UIContextMenuComponent.ItemDefaultTextColor;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.BoundableTransform.Margins = new Vector4(UIContextMenuComponent.ItemPadding, 0, 0, 0);
                button.InteractAction += _ => Activate(transform);
                return transform;
            }

            private void Activate(UIBoundableTransform row)
            {
                UIListTransform? list = List;
                if (list is null || !list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease) ||
                    !list.TryGetVirtualRowIndex(row, out int index))
                    return;
                UIComboOption option = source.GetOption(index);
                if (!option.IsEnabled || option.Id != lease.ItemId || !owner.Select(option.Id, EUIValueChangeOrigin.User))
                    return;
                owner.Close();
            }

            public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
            {
                UIComboOption option = source.GetOption(index);
                if (option.Id != itemId)
                    throw new InvalidOperationException("Combo option ID changed during row binding.");
                UIButtonComponent button = row.SceneNode!.GetComponent<UIButtonComponent>()!;
                button.IsEnabled = option.IsEnabled;
                button.AccessibilityLabel = option.Label;
                row.SceneNode.FirstChild!.GetComponent<UITextComponent>()!.Text = option.Label;
            }

            public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
            {
                UIButtonComponent button = row.SceneNode!.GetComponent<UIButtonComponent>()!;
                button.AccessibilityLabel = null;
                row.SceneNode.FirstChild!.GetComponent<UITextComponent>()!.Text = string.Empty;
            }
        }
        [Category("Events")]
        public event DelScrolling? Scrolled;

        protected void OnScrolled(bool up)
            => Scrolled?.Invoke(up);

    }
}

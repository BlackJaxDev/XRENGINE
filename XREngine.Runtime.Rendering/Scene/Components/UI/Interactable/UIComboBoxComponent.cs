using System.ComponentModel;
using System.Numerics;
using XREngine.Input.Devices;
using XREngine.Input;
using XREngine.Rendering.UI.State;
using XREngine.Data.Colors;
using XREngine.Rendering.Models.Materials;
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
            SelectAdjacent(direction.Y > 0 ? 1 : -1);
            return true;
        }
        public override bool AccessibilityActivate() => Open();
        public override bool AccessibilityAdjust(int direction)
        {
            if (direction == 0 || !CanInteract || IsReadOnly)
                return false;
            ulong? previous = SelectedId;
            SelectAdjacent(Math.Sign(direction));
            return previous != SelectedId;
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
                if (ReferenceEquals(_popup, value))
                    return;
                Close();
                if (IsActiveInHierarchy && _popup is not null)
                    _popup.Dismissed -= OnPopupDismissed;
                SetField(ref _popup, value);
                if (IsActiveInHierarchy && _popup is not null)
                    _popup.Dismissed += OnPopupDismissed;
            }
        }

        public Vector2 PopupPosition { get; set; }
        public ulong? SelectedId => _selectedId;
        /// <summary>Allows the host to place the popup before opening it.</summary>
        public event Action<UIComboBoxComponent>? Opening;
        public event Action<UIValueChange<ulong?>>? SelectionChanged;

        /// <summary>Uses a virtual popup for a large source with stable IDs.</summary>
        public IUIComboOptionSource? VirtualOptions
        {
            get => _virtualOptions;
            set
            {
                if (ReferenceEquals(_virtualOptions, value))
                    return;
                Close();
                SetField(ref _virtualOptions, value);
                _virtualAdapter = value is null ? null : new VirtualOptionAdapter(this, value);
                if (_selectedId is { } id && !HasOption(id))
                    Select(null, EUIValueChangeOrigin.Programmatic);
            }
        }

        public IUIValueBinding<ulong?>? Binding
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
                    Select(_binding.Value, EUIValueChangeOrigin.Binding);
            }
        }

        public void SetOptions(ReadOnlySpan<UIComboOption> options)
        {
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
            if (id is { } selected && !HasOption(selected))
                return false;
            if (_binding is not null && origin != EUIValueChangeOrigin.Binding)
            {
                _writingBinding = true;
                try { _binding.SetValue(id); }
                finally { _writingBinding = false; }
                id = _binding.Value;
            }
            if (_selectedId == id)
                return true;
            ulong? previous = _selectedId;
            SetField(ref _selectedId, id, nameof(SelectedId));
            SelectionChanged?.Invoke(new(previous, id, origin));
            return true;
        }

        private void OnBindingChanged(ulong? id)
        {
            if (!_writingBinding)
                Select(id, EUIValueChangeOrigin.Binding);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(SelectedId) && SceneNode is not null && !SceneNode.IsTransformNull)
                UITransform.RecordUIChange(UIChangeDomain.Visual);
        }

        public bool Open()
        {
            if (!CanInteract || IsReadOnly || _popup is null)
                return false;
            if (_virtualAdapter is not null)
            {
                if (_virtualAdapter.Count == 0)
                    return false;
                Opening?.Invoke(this);
                _popup.ShowVirtual(PopupPosition, _virtualAdapter);
                _virtualAdapter.List = _popup.VirtualList;
                _restoreFocusOnDismiss = true;
                return true;
            }
            if (_options.Length == 0)
                return false;
            Opening?.Invoke(this);
            ContextMenuItem[] items = new ContextMenuItem[_options.Length];
            for (int i = 0; i < _options.Length; i++)
            {
                UIComboOption option = _options[i];
                items[i] = new ContextMenuItem(option.Label,
                    () => Select(option.Id, EUIValueChangeOrigin.User), option.IsEnabled);
            }
            _popup.Show(PopupPosition, items);
            _restoreFocusOnDismiss = true;
            return true;
        }

        public void Close()
        {
            _restoreFocusOnDismiss = false;
            _popup?.Hide();
        }

        private void OnPopupDismissed()
        {
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
            input.RegisterKeyStateChange(EKey.Down, SelectNext);
            input.RegisterKeyStateChange(EKey.Up, SelectPrevious);
            input.RegisterKeyStateChange(EKey.Escape, Escape);
            input.RegisterKeyStateChange(EKey.Enter, OpenKey);
            input.RegisterKeyStateChange(EKey.Space, OpenKey);
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

        private void SelectAdjacent(int direction)
        {
            if (!CanInteract || IsReadOnly || _virtualOptions is null && _options.Length == 0)
                return;
            int index = -1;
            if (_virtualOptions is not null)
            {
                int count = _virtualOptions.Count;
                if (count == 0)
                    return;
                index = _selectedId is { } id ? _virtualOptions.IndexOfId(id) : -1;
                if (index < 0 && direction < 0)
                    index = 0;
                for (int offset = 1; offset <= count; offset++)
                {
                    int candidate = (index + direction * offset + count * 2) % count;
                    UIComboOption option = _virtualOptions.GetOption(candidate);
                    if (option.IsEnabled)
                    {
                        Select(option.Id, EUIValueChangeOrigin.User);
                        return;
                    }
                }
                return;
            }
            for (int i = 0; i < _options.Length; i++)
                if (_options[i].Id == _selectedId)
                    index = i;
            if (index < 0 && direction < 0)
                index = 0;
            for (int offset = 1; offset <= _options.Length; offset++)
            {
                int candidate = (index + direction * offset + _options.Length * 2) % _options.Length;
                if (_options[candidate].IsEnabled)
                {
                    Select(_options[candidate].Id, EUIValueChangeOrigin.User);
                    return;
                }
            }
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
                XRMaterial material = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.DarkGray);
                material.EnableTransparency();
                background.Material = material;
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

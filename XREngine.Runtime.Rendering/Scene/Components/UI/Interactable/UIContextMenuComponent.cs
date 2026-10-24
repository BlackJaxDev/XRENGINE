using System.Numerics;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// A single entry in a <see cref="UIContextMenuComponent"/>.
    /// </summary>
    public sealed class ContextMenuItem
    {
        /// <summary>
        /// Display label for this menu item.
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// Action to invoke when the item is clicked.
        /// </summary>
        public Action? Action { get; }

        /// <summary>
        /// Whether this item is currently enabled and clickable.
        /// </summary>
        public bool Enabled { get; }

        /// <summary>
        /// True if this item represents a visual separator rather than an actionable entry.
        /// </summary>
        public bool IsSeparator { get; }

        public ContextMenuItem(string label, Action? action, bool enabled = true)
        {
            Label = label;
            Action = action;
            Enabled = enabled;
            IsSeparator = false;
        }

        private ContextMenuItem()
        {
            Label = string.Empty;
            Action = null;
            Enabled = false;
            IsSeparator = true;
        }

        /// <summary>
        /// Creates a separator menu item.
        /// </summary>
        public static ContextMenuItem Separator() => new();
    }

    /// <summary>
    /// A native UI context menu that can be shown at an arbitrary canvas position.
    /// Renders as a vertical list of <see cref="UIButtonComponent"/> items.
    /// Auto-dismisses on item click, click outside, or Escape key.
    /// </summary>
    [RequiresTransform(typeof(UIBoundableTransform))]
    public class UIContextMenuComponent : UIComponent
    {
        // --- Style constants (may be overridden or moved to EditorStyles later) ---
        public static ColorF4 BackgroundColor { get; set; } = new(0.15f, 0.15f, 0.15f, 0.95f);
        public static ColorF4 ItemDefaultTextColor { get; set; } = ColorF4.White;
        public static ColorF4 ItemDisabledTextColor { get; set; } = new(0.5f, 0.5f, 0.5f, 1.0f);
        public static ColorF4 ItemHighlightBackground { get; set; } = new(0.3f, 0.5f, 0.8f, 0.6f);
        public static ColorF4 SeparatorColor { get; set; } = new(0.35f, 0.35f, 0.35f, 0.8f);

        public static float ItemHeight { get; set; } = 22.0f;
        public static float SeparatorHeight { get; set; } = 4.0f;
        public static float MenuWidth { get; set; } = 160.0f;
        public static float FontSize { get; set; } = 12.0f;
        public static float ItemPadding { get; set; } = 6.0f;
        private const int BackgroundZIndex = -30;
        private const int ItemBackgroundZIndex = -20;
        private const int SeparatorZIndex = -15;
        private const int LabelZIndex = -10;

        private ContextMenuItem[] _items = [];
        private readonly List<UIContextMenuItemComponent> _itemButtons = [];
        private UICanvasTransform? _placementCanvas;
        private float _itemHeight = ItemHeight;
        private float _separatorHeight = SeparatorHeight;
        private IVirtualListProvider? _virtualProvider;
        private Action<SceneNode>? _contentBuilder;
        private float _contentHeight;
        private float _requestedHeight;
        private UIListTransform? _virtualList;
        private bool _isOpen;
        private IUICanvasInputSource? _subscribedInput;
        private Vector2 _pendingPosition;
        private ulong _layoutGenerationAtRequest;
        private bool _awaitingPlacement;
        private bool _tearingDown;
        private bool _clearingMenuItems;
        private bool _hiding;
        private ulong _showRevision;

        /// <summary>
        /// Whether the context menu is currently visible and accepting input.
        /// </summary>
        public bool IsOpen
        {
            get => _isOpen;
            private set
            {
                if (!SetField(ref _isOpen, value) || SceneNode is null || SceneNode.IsTransformNull)
                    return;
                UITransform.RecordUIChange(UIChangeDomain.HitTest);
            }
        }

        protected override UIChangeDomain GetUIChangeDomain(string? propertyName)
            => propertyName == nameof(IsOpen)
                ? UIChangeDomain.HitTest
                : base.GetUIChangeDomain(propertyName);

        /// <summary>
        /// Fired after the menu is dismissed (by item click, outside click, or Escape).
        /// </summary>
        public event Action? Dismissed;

        /// <summary>
        /// Shows the context menu at the given parent-local top-left position with the specified items.
        /// </summary>
        /// <param name="parentLocalPosition">Position in parent-local coordinates (top-left of menu).</param>
        /// <param name="items">Menu items to display.</param>
        public void Show(Vector2 parentLocalPosition, params ContextMenuItem[] items)
            => ShowCore(parentLocalPosition, items, null);

        /// <summary>Opens a bounded popup backed by a virtual list provider.</summary>
        public void ShowVirtual(Vector2 parentLocalPosition, IVirtualListProvider provider)
            => ShowCore(parentLocalPosition, [], provider);

        /// <summary>Shows bounded controls supplied by the caller in the menu's list.</summary>
        public void ShowContent(Vector2 parentLocalPosition, float height, Action<SceneNode> buildContent)
        {
            ArgumentNullException.ThrowIfNull(buildContent);
            if (!float.IsFinite(height) || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            ShowCore(parentLocalPosition, [], null, buildContent, height);
        }

        /// <summary>Shows a non-interactive hint through the same accepted-layout placement path.</summary>
        public void ShowPassiveContent(Vector2 parentLocalPosition, float height, Action<SceneNode> buildContent)
        {
            ArgumentNullException.ThrowIfNull(buildContent);
            if (!float.IsFinite(height) || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            ShowCore(parentLocalPosition, [], null, buildContent, height, passive: true);
        }

        public float PreferredWidth { get; set; } = MenuWidth;

        public UIListTransform? VirtualList => _virtualList;

        private void ShowCore(Vector2 parentLocalPosition, ContextMenuItem[] items, IVirtualListProvider? provider,
            Action<SceneNode>? contentBuilder = null, float contentHeight = 0, bool passive = false)
        {
            if (_tearingDown || IsDestroyed || _clearingMenuItems || _hiding)
                return;
            ArgumentNullException.ThrowIfNull(items);
            for (int index = 0; index < items.Length; index++)
                ArgumentNullException.ThrowIfNull(items[index], nameof(items));
            ulong showRevision = ++_showRevision;
            _ = BoundableTransform.GetCanvasTransform() ??
                throw new InvalidOperationException("A context menu needs an attached UI canvas.");
            // The caller keeps its array. Copy it and the row sizes so that callbacks cannot
            // change the rows after the height is computed.
            items = items.Length == 0 ? [] : (ContextMenuItem[])items.Clone();
            float itemHeight = ItemHeight;
            float separatorHeight = SeparatorHeight;
            Hide(); // Dismiss any existing menu first
            if (_tearingDown || IsDestroyed || showRevision != _showRevision)
                return;
            ClearMenuItems(); // Remove content retained when the owner deactivated.
            if (_tearingDown || IsDestroyed || showRevision != _showRevision)
                return;

            if ((items.Length == 0 && provider is null && contentBuilder is null) || provider is { Count: <= 0 })
                return;
            if (_tearingDown || IsDestroyed || showRevision != _showRevision)
                return;

            _items = items;
            _itemHeight = itemHeight;
            _separatorHeight = separatorHeight;
            _virtualProvider = provider;
            _contentBuilder = contentBuilder;
            _contentHeight = contentHeight;
            IsOpen = true;
            if (_tearingDown || IsDestroyed || showRevision != _showRevision || !_isOpen)
                return;

            // Make the node visible (it's collapsed when hidden)
            BoundableTransform.Visibility = EVisibility.Hidden;

            // Position the menu at the given canvas location
            var tfm = BoundableTransform;
            tfm.MinAnchor = Vector2.Zero;
            tfm.MaxAnchor = Vector2.Zero;
            tfm.NormalizedPivot = new Vector2(0.0f, 1.0f); // top-left pivot
            tfm.Width = PreferredWidth;
            tfm.BlocksInputBehind = !passive;
            tfm.Margins = Vector4.Zero;
            tfm.Padding = Vector4.Zero;

            // Calculate total height
            float totalHeight = 0f;
            if (contentBuilder is not null)
                totalHeight = contentHeight;
            else if (provider is not null)
                totalHeight = Math.Min(provider.Count, 8) * itemHeight;
            else
                foreach (var item in items)
                    totalHeight += item.IsSeparator ? separatorHeight : itemHeight;
            tfm.Height = totalHeight;
            if (_tearingDown || IsDestroyed || showRevision != _showRevision || !_isOpen)
                return;
            // Property callbacks can move the menu. Use the canvas it is attached to now.
            if (tfm.GetCanvasTransform() is not { } canvas)
            {
                Hide();
                return;
            }
            _requestedHeight = totalHeight;
            _pendingPosition = parentLocalPosition;
            _placementCanvas = canvas;
            _layoutGenerationAtRequest = canvas.CurrentLayoutGeneration;
            _awaitingPlacement = true;

            // Set render pass to ensure we draw on top
            var bgMat = SceneNode.GetComponent<UIMaterialComponent>();
            if (bgMat is null)
            {
                bgMat = SceneNode.AddComponent<UIMaterialComponent>()!;
                bgMat.SetBatchSolidColor(BackgroundColor);
            }
            else
            {
                bgMat.SetQuadColor("MatColor", BackgroundColor);
            }
            bgMat.RenderCommand2D.ZIndex = BackgroundZIndex;
            bgMat.ClipToBounds = true;

            BuildMenuItems();
            if (_tearingDown || IsDestroyed || showRevision != _showRevision || !_isOpen)
                return;
            tfm.InvalidateLayout();
            if (!ReferenceEquals(tfm.GetCanvasTransform(), _placementCanvas))
            {
                Hide();
                return;
            }
            canvas.UpdateLayoutAsync();
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, CompletePlacement);
            if (!passive)
                SubscribeForDismiss();
        }

        private void CompletePlacement()
        {
            if (!_isOpen || !_awaitingPlacement)
                return;
            UIBoundableTransform transform = BoundableTransform;
            UICanvasTransform? canvas = transform.GetCanvasTransform();
            if (canvas is not null && !ReferenceEquals(canvas, _placementCanvas))
            {
                // A generation from another canvas does not describe this placement.
                _placementCanvas = canvas;
                _layoutGenerationAtRequest = canvas.CurrentLayoutGeneration;
                canvas.UpdateLayoutAsync();
                return;
            }
            if (canvas is null || canvas.CurrentLayoutGeneration <= _layoutGenerationAtRequest ||
                transform.Parent is not UIBoundableTransform parent ||
                parent.ActualWidth <= 0 || parent.ActualHeight <= 0)
                return;
            float boundedHeight = MathF.Min(_requestedHeight, parent.ActualHeight);
            if (transform.Height != boundedHeight)
            {
                transform.Height = boundedHeight;
                _layoutGenerationAtRequest = canvas.CurrentLayoutGeneration;
                canvas.UpdateLayoutAsync();
                return;
            }
            _awaitingPlacement = false;
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CompletePlacement);
            transform.Translation = ClampMenuPosition(_pendingPosition, PreferredWidth, boundedHeight);
            transform.Visibility = EVisibility.Visible;
            canvas.UpdateLayoutAsync();
        }

        private Vector2 ClampMenuPosition(Vector2 position, float width, float height)
        {
            if (BoundableTransform.Parent is not UIBoundableTransform parent)
                return position;

            Vector2 parentSize = parent.ActualSize;
            if (parentSize.X <= 0.0f || parentSize.Y <= 0.0f)
                return position;

            float maxX = MathF.Max(0.0f, parentSize.X - width);
            float minY = MathF.Min(parentSize.Y, height);
            return new Vector2(
                Math.Clamp(position.X, 0.0f, maxX),
                Math.Clamp(position.Y, minY, parentSize.Y));
        }

        /// <summary>
        /// Hides and cleans up the context menu.
        /// </summary>
        public void Hide()
            => HideCore(clearMenuItems: true);

        private void HideCore(bool clearMenuItems)
        {
            if (!_isOpen || _tearingDown || _hiding)
                return;
            _hiding = true;
            try
            {
                IsOpen = false;
                if (_isOpen || _tearingDown || IsDestroyed)
                    return;
                _awaitingPlacement = false;
                UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CompletePlacement);
                UnsubscribeForDismiss();
                if (clearMenuItems)
                    ClearMenuItems();
                if (_tearingDown || IsDestroyed)
                    return;
                _items = [];
                _virtualProvider = null;
                _contentBuilder = null;
                _contentHeight = 0;
                _requestedHeight = 0;
                _virtualList = null;
                _placementCanvas = null;
                ReleaseItemButtons();

                // Collapse the node so the background material stops rendering.
                BoundableTransform.Visibility = EVisibility.Collapsed;
            }
            finally
            {
                _hiding = false;
            }
            if (!_tearingDown && !IsDestroyed)
                Dismissed?.Invoke();
        }

        private void BuildMenuItems()
        {
            bool ordinaryItems = _contentBuilder is null && _virtualProvider is null;
            SceneNode listParent = SceneNode;
            if (ordinaryItems)
            {
                SceneNode scrollNode = SceneNode.NewChild();
                scrollNode.Name = "ContextMenuScroll";
                UIScrollableTransform scroll = scrollNode.SetTransform<UIScrollableTransform>();
                scroll.MinAnchor = Vector2.Zero;
                scroll.MaxAnchor = Vector2.One;
                scroll.Width = null;
                scroll.Height = null;
                scroll.ScrollableX = false;
                UIScrollRegionComponent scrollInput = scrollNode.AddComponent<UIScrollRegionComponent>()!;
                scrollInput.IsTabStop = false;
                scrollInput.ConsumeWheelAtBoundary = true;
                listParent = scrollNode;
            }
            var listNode = listParent.NewChild();
            listNode.Name = "ContextMenuList";
            var listTfm = listNode.SetTransform<UIListTransform>();
            listTfm.DisplayHorizontal = false;
            listTfm.ItemSpacing = 0;
            listTfm.Padding = new Vector4(0.0f);
            listTfm.Margins = Vector4.Zero;
            listTfm.MinAnchor = Vector2.Zero;
            listTfm.MaxAnchor = Vector2.One;
            listTfm.Translation = Vector2.Zero;
            listTfm.Width = null;
            listTfm.Height = null;
            listTfm.ItemAlignment = EListAlignment.TopOrLeft;
            if (ordinaryItems)
            {
                listTfm.MinAnchor = new Vector2(0, 1);
                listTfm.MaxAnchor = Vector2.One;
                listTfm.NormalizedPivot = new Vector2(0, 1);
                listTfm.Height = _requestedHeight;
            }

            if (_contentBuilder is not null)
            {
                _contentBuilder(listNode);
                return;
            }

            if (_virtualProvider is not null)
            {
                listTfm.ItemSize = _itemHeight;
                listTfm.VirtualOverscan = 2;
                listTfm.VirtualProvider = _virtualProvider;
                _virtualList = listTfm;
                UIVirtualPopupScrollComponent scroll = listNode.AddComponent<UIVirtualPopupScrollComponent>()!;
                scroll.List = listTfm;
                return;
            }

            foreach (var item in _items)
            {
                if (item.IsSeparator)
                {
                    BuildSeparator(listNode);
                }
                else
                {
                    BuildMenuItem(listNode, item);
                }
            }
        }

        private void BuildMenuItem(SceneNode parent, ContextMenuItem item)
        {
            var buttonNode = parent.NewChild<UIContextMenuItemComponent, UIMaterialComponent>(out var button, out var background);

            background.SetBatchSolidColor(ColorF4.Transparent);
            background.RenderCommand2D.ZIndex = ItemBackgroundZIndex;

            var buttonTfm = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
            buttonTfm.MinAnchor = new Vector2(0.0f, 0.0f);
            buttonTfm.MaxAnchor = new Vector2(1.0f, 0.0f);
            buttonTfm.Height = _itemHeight;
            buttonTfm.Margins = new Vector4(0.0f);

            ulong showRevision = _showRevision;
            button.Bind(this, showRevision);
            if (item.Enabled)
            {
                _itemButtons.Add(button);
                button.DefaultBackgroundColor = ColorF4.Transparent;
                button.HighlightBackgroundColor = ItemHighlightBackground;
                button.DefaultTextColor = ItemDefaultTextColor;
                button.HighlightTextColor = ItemDefaultTextColor;
                button.RegisterClickActions(_ =>
                {
                    if (_tearingDown || IsDestroyed || !_isOpen ||
                        showRevision != _showRevision || !button.CanInteract ||
                        !ReferenceEquals(button.Menu, this))
                        return;
                    item.Action?.Invoke();
                    if (!_tearingDown && !IsDestroyed && _isOpen && showRevision == _showRevision)
                        Hide();
                });
            }
            else
            {
                button.IsEnabled = false;
                button.IsTabStop = false;
                button.DefaultBackgroundColor = ColorF4.Transparent;
                button.HighlightBackgroundColor = ColorF4.Transparent;
                button.DefaultTextColor = ItemDisabledTextColor;
                button.HighlightTextColor = ItemDisabledTextColor;
            }

            buttonNode.NewChild<UITextComponent>(out var label);
            label.Text = item.Label;
            label.FontSize = FontSize;
            label.Color = item.Enabled ? ItemDefaultTextColor : ItemDisabledTextColor;
            label.HorizontalAlignment = EHorizontalAlignment.Left;
            label.VerticalAlignment = EVerticalAlignment.Center;
            label.BoundableTransform.Margins = new Vector4(ItemPadding, 0.0f, ItemPadding, 0.0f);
            label.RenderCommand2D.ZIndex = LabelZIndex;
        }

        private void BuildSeparator(SceneNode parent)
        {
            var sepNode = parent.NewChild<UIMaterialComponent>(out var background);
            var sepTfm = sepNode.GetTransformAs<UIBoundableTransform>(true)!;
            sepTfm.MinAnchor = new Vector2(0.0f, 0.0f);
            sepTfm.MaxAnchor = new Vector2(1.0f, 0.0f);
            sepTfm.Height = _separatorHeight;
            sepTfm.Margins = new Vector4(0.0f);

            // Draw a thin horizontal line in the middle
            var lineNode = sepNode.NewChild<UIMaterialComponent>(out var line);
            var lineTfm = lineNode.GetTransformAs<UIBoundableTransform>(true)!;
            lineTfm.MinAnchor = new Vector2(0.0f, 0.5f);
            lineTfm.MaxAnchor = new Vector2(1.0f, 0.5f);
            lineTfm.Height = 1.0f;
            lineTfm.Margins = new Vector4(ItemPadding, 0.0f, ItemPadding, 0.0f);
            line.SetBatchSolidColor(SeparatorColor);
            line.RenderCommand2D.ZIndex = SeparatorZIndex;
        }

        private void ClearMenuItems()
        {
            if (_clearingMenuItems || _tearingDown || IsDestroyed)
                return;
            _clearingMenuItems = true;
            ReleaseItemButtons();
            try
            {
                // Menu content owns its nodes. Destroy them through the scene hierarchy so
                // their components unsubscribe before the guarded child list is changed.
                while (!_tearingDown && !IsDestroyed && SceneNode.Transform.Children.Count > 0)
                {
                    var child = SceneNode.Transform.Children[^1];
                    if (child.SceneNode is { } node)
                        node.Destroy(true);
                    else
                        child.Parent = null;
                }
            }
            finally
            {
                _clearingMenuItems = false;
            }
        }

        /// <summary>
        /// Moves focus from a row of the current show to another enabled row. Returns false
        /// when the row belongs to an earlier show or no other row accepts presented focus.
        /// </summary>
        internal bool MoveItemFocus(UIContextMenuItemComponent from, ulong showRevision,
            int direction, bool toEdge)
        {
            // Rows of a menu that is not placed yet are not presented, so presented focus rejects them.
            if (_tearingDown || IsDestroyed || !_isOpen || direction == 0 ||
                showRevision != _showRevision || !ReferenceEquals(from.Menu, this))
                return false;
            int count = _itemButtons.Count;
            int index = _itemButtons.IndexOf(from);
            if (index < 0 || UITransform.GetCanvasComponent()?.GetInputComponent() is not { } input)
                return false;
            int step = direction > 0 ? 1 : -1;
            // Home and End search inward from an edge. Arrows search outward and wrap.
            int attempts = toEdge ? count : count - 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                int candidate = toEdge
                    ? (step > 0 ? count - 1 - attempt : attempt)
                    : ((index + step * (attempt + 1)) % count + count) % count;
                UIContextMenuItemComponent target = _itemButtons[candidate];
                if (ReferenceEquals(target, from))
                    return false;
                if (!target.CanInteract)
                    continue;
                if (input.TryFocusPresented(target))
                    return showRevision == _showRevision && _isOpen;
                // A focus callback can hide or replace this menu.
                if (showRevision != _showRevision || !_isOpen || _itemButtons.Count != count)
                    return false;
            }
            return false;
        }

        private void ReleaseItemButtons()
        {
            for (int index = 0; index < _itemButtons.Count; index++)
                _itemButtons[index].Release();
            _itemButtons.Clear();
        }

        // --- Auto-dismiss logic ---

        private bool _dismissClickArmed;
        private bool _armDismissClickSubscribed;

        private void SubscribeForDismiss()
        {
            // Find the UICanvasInputComponent in the canvas hierarchy
            var canvas = UITransform.GetCanvasComponent();
            _subscribedInput = canvas?.GetInputComponent();

            if (_subscribedInput is not null)
            {
                _subscribedInput.EscapePressed += OnEscapeDismiss;
                _subscribedInput.RightClick += OnOutsideRightClick;
                _subscribedInput.LeftClickDown += OnLeftClickDown;
                _subscribedInput.BackPressed += OnGamepadBackPressed;
            }

            // Delay one frame before listening for left-clicks so the same click that
            // opened the menu doesn't immediately dismiss it.
            SetField(ref _dismissClickArmed, false, publishNotifications: false);
            if (!_armDismissClickSubscribed)
            {
                RuntimeEngine.Time.Timer.PostUpdateFrame += ArmDismissClick;
                SetField(ref _armDismissClickSubscribed, true, publishNotifications: false);
            }
        }

        private void UnsubscribeForDismiss()
        {
            if (_subscribedInput is not null)
            {
                _subscribedInput.EscapePressed -= OnEscapeDismiss;
                _subscribedInput.RightClick -= OnOutsideRightClick;
                _subscribedInput.LeftClickDown -= OnLeftClickDown;
                _subscribedInput.BackPressed -= OnGamepadBackPressed;
                _subscribedInput = null;
            }
            UnsubscribeArmDismissClick();
        }

        private void UnsubscribeArmDismissClick()
        {
            if (!_armDismissClickSubscribed)
                return;

            // Post-update removals are deferred; a second removal could also remove
            // the callback registered by a menu reopened before the next frame.
            SetField(ref _armDismissClickSubscribed, false, publishNotifications: false);
            RuntimeEngine.Time.Timer.PostUpdateFrame -= ArmDismissClick;
        }

        /// <summary>
        /// One-shot handler that arms outside-click detection on the frame after the menu opens.
        /// </summary>
        private void ArmDismissClick()
        {
            UnsubscribeArmDismissClick();
            if (!_isOpen)
                return;
            SetField(ref _dismissClickArmed, true, publishNotifications: false);
        }

        private void OnEscapeDismiss()
        {
            Hide();
        }

        private void OnGamepadBackPressed(UIInteractableComponent? focused)
        {
            if (!_isOpen || focused?.SceneNode is not { } focusedNode)
                return;
            // Only the closest open menu owns Back, including when menus are nested.
            for (SceneNode? node = focusedNode; node is not null; node = node.Parent)
                if (node.GetComponent<UIContextMenuComponent>() is { IsOpen: true } menu)
                {
                    if (ReferenceEquals(menu, this))
                        Hide();
                    return;
                }
        }

        private void OnOutsideRightClick(UIInteractableComponent _)
        {
            // Any right-click while the menu is open dismisses it.
            Hide();
        }

        private void OnLeftClickDown(UIInteractableComponent? clicked)
        {
            if (!_isOpen || !_dismissClickArmed)
                return;

            // If clicked on nothing (empty space), dismiss.
            if (clicked is null)
            {
                Hide();
                return;
            }

            // If clicked inside our menu subtree, the button handler will call Hide().
            if (IsDescendantOfSelf(clicked.SceneNode))
                return;

            // Otherwise it's an outside click — dismiss.
            Hide();
        }

        private bool IsDescendantOfSelf(SceneNode? candidate)
        {
            for (var node = candidate; node is not null; node = node.Parent)
            {
                if (ReferenceEquals(node, SceneNode))
                    return true;
            }
            return false;
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            if (_isOpen)
                HideCore(clearMenuItems: false);
        }

        protected override void OnDestroying()
        {
            _tearingDown = true;
            _showRevision++;
            _awaitingPlacement = false;
            _isOpen = false;
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CompletePlacement);
            UnsubscribeForDismiss();
            _items = [];
            _virtualProvider = null;
            _contentBuilder = null;
            _virtualList = null;
            _placementCanvas = null;
            ReleaseItemButtons();
            Dismissed = null;
            base.OnDestroying();
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T value)
        {
            if (_tearingDown || IsDestroyed)
                return false;
            ulong showRevision = _showRevision;
            bool allowed = base.OnPropertyChanging(propName, field, value);
            return allowed && !_tearingDown && !IsDestroyed &&
                (propName != nameof(IsOpen) || showRevision == _showRevision);
        }

        private UIBoundableTransform BoundableTransform => TransformAs<UIBoundableTransform>(true)!;
    }
}

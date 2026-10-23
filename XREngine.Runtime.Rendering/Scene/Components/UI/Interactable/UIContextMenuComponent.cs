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
        private IVirtualListProvider? _virtualProvider;
        private Action<SceneNode>? _contentBuilder;
        private float _contentHeight;
        private UIListTransform? _virtualList;
        private bool _isOpen;
        private IUICanvasInputSource? _subscribedInput;
        private Vector2 _pendingPosition;
        private ulong _layoutGenerationAtRequest;
        private bool _awaitingPlacement;

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
            UICanvasTransform canvas = BoundableTransform.GetCanvasTransform() ??
                throw new InvalidOperationException("A context menu needs an attached UI canvas.");
            Hide(); // Dismiss any existing menu first

            if ((items.Length == 0 && provider is null && contentBuilder is null) || provider is { Count: <= 0 })
                return;

            _items = items;
            _virtualProvider = provider;
            _contentBuilder = contentBuilder;
            _contentHeight = contentHeight;
            IsOpen = true;

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
                totalHeight = Math.Min(provider.Count, 8) * ItemHeight;
            else
                foreach (var item in items)
                    totalHeight += item.IsSeparator ? SeparatorHeight : ItemHeight;
            tfm.Height = totalHeight;
            _pendingPosition = parentLocalPosition;
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
            tfm.InvalidateLayout();
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
            if (canvas is null || canvas.CurrentLayoutGeneration <= _layoutGenerationAtRequest ||
                transform.Parent is not UIBoundableTransform parent ||
                parent.ActualWidth <= 0 || parent.ActualHeight <= 0)
                return;
            _awaitingPlacement = false;
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CompletePlacement);
            transform.Translation = ClampMenuPosition(_pendingPosition, PreferredWidth, transform.Height ?? 0);
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
        {
            if (!_isOpen)
                return;

            IsOpen = false;
            _awaitingPlacement = false;
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CompletePlacement);
            UnsubscribeForDismiss();
            ClearMenuItems();
            _items = [];
            _virtualProvider = null;
            _contentBuilder = null;
            _contentHeight = 0;
            _virtualList = null;

            // Collapse the node so the background material stops rendering
            BoundableTransform.Visibility = EVisibility.Collapsed;

            Dismissed?.Invoke();
        }

        private void BuildMenuItems()
        {
            var listNode = SceneNode.NewChild();
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

            if (_contentBuilder is not null)
            {
                _contentBuilder(listNode);
                return;
            }

            if (_virtualProvider is not null)
            {
                listTfm.ItemSize = ItemHeight;
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
            var buttonNode = parent.NewChild<UIButtonComponent, UIMaterialComponent>(out var button, out var background);

            background.SetBatchSolidColor(ColorF4.Transparent);
            background.RenderCommand2D.ZIndex = ItemBackgroundZIndex;

            var buttonTfm = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
            buttonTfm.MinAnchor = new Vector2(0.0f, 0.0f);
            buttonTfm.MaxAnchor = new Vector2(1.0f, 0.0f);
            buttonTfm.Height = ItemHeight;
            buttonTfm.Margins = new Vector4(0.0f);

            if (item.Enabled)
            {
                button.DefaultBackgroundColor = ColorF4.Transparent;
                button.HighlightBackgroundColor = ItemHighlightBackground;
                button.DefaultTextColor = ItemDefaultTextColor;
                button.HighlightTextColor = ItemDefaultTextColor;
                button.RegisterClickActions(_ =>
                {
                    item.Action?.Invoke();
                    Hide();
                });
            }
            else
            {
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

        private static void BuildSeparator(SceneNode parent)
        {
            var sepNode = parent.NewChild<UIMaterialComponent>(out var background);
            var sepTfm = sepNode.GetTransformAs<UIBoundableTransform>(true)!;
            sepTfm.MinAnchor = new Vector2(0.0f, 0.0f);
            sepTfm.MaxAnchor = new Vector2(1.0f, 0.0f);
            sepTfm.Height = SeparatorHeight;
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
            // Menu content owns its nodes. Destroy them through the scene hierarchy so
            // their components unsubscribe before the guarded child list is changed.
            while (SceneNode.Transform.Children.Count > 0)
            {
                var child = SceneNode.Transform.Children[^1];
                if (child.SceneNode is { } node)
                    node.Destroy(true);
                else
                    child.Parent = null;
            }
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
                Hide();
        }

        private UIBoundableTransform BoundableTransform => TransformAs<UIBoundableTransform>(true)!;
    }
}

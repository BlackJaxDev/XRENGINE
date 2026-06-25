using System.Numerics;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

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
        private bool _isOpen;
        private UICanvasInputComponent? _subscribedInput;

        /// <summary>
        /// Whether the context menu is currently visible and accepting input.
        /// </summary>
        public bool IsOpen
        {
            get => _isOpen;
            private set => SetField(ref _isOpen, value);
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
        {
            Hide(); // Dismiss any existing menu first

            if (items.Length == 0)
                return;

            _items = items;
            IsOpen = true;

            // Make the node visible (it's collapsed when hidden)
            BoundableTransform.Visibility = EVisibility.Visible;

            // Position the menu at the given canvas location
            var tfm = BoundableTransform;
            tfm.MinAnchor = Vector2.Zero;
            tfm.MaxAnchor = Vector2.Zero;
            tfm.NormalizedPivot = new Vector2(0.0f, 1.0f); // top-left pivot
            tfm.Width = MenuWidth;
            tfm.BlocksInputBehind = true;
            tfm.Margins = Vector4.Zero;
            tfm.Padding = Vector4.Zero;

            // Calculate total height
            float totalHeight = 0f;
            foreach (var item in items)
                totalHeight += item.IsSeparator ? SeparatorHeight : ItemHeight;
            tfm.Height = totalHeight;
            tfm.Translation = ClampMenuPosition(parentLocalPosition, MenuWidth, totalHeight);

            // Set render pass to ensure we draw on top
            var bgMat = SceneNode.GetComponent<UIMaterialComponent>();
            if (bgMat is null)
            {
                bgMat = SceneNode.AddComponent<UIMaterialComponent>()!;
                var mat = XRMaterial.CreateUnlitColorMaterialForward(BackgroundColor);
                mat.EnableTransparency();
                bgMat.Material = mat;
                bgMat.RenderPass = (int)EDefaultRenderPass.TransparentForward;
            }
            else
            {
                bgMat.Material?.SetVector4("MatColor", BackgroundColor);
            }
            bgMat.RenderCommand2D.ZIndex = BackgroundZIndex;

            BuildMenuItems();
            tfm.InvalidateLayout();
            tfm.GetCanvasTransform()?.UpdateLayout();
            SubscribeForDismiss();
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
            UnsubscribeForDismiss();
            ClearMenuItems();
            _items = [];

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

            var mat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Transparent);
            mat.EnableTransparency();
            background.Material = mat;
            background.RenderPass = (int)EDefaultRenderPass.TransparentForward;
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
            var lineMat = XRMaterial.CreateUnlitColorMaterialForward(SeparatorColor);
            lineMat.EnableTransparency();
            line.Material = lineMat;
            line.RenderPass = (int)EDefaultRenderPass.TransparentForward;
            line.RenderCommand2D.ZIndex = SeparatorZIndex;
        }

        private void ClearMenuItems()
        {
            // Remove all children created for menu items
            SceneNode.Transform.Clear();
        }

        // --- Auto-dismiss logic ---

        private bool _dismissClickArmed;

        private void SubscribeForDismiss()
        {
            // Find the UICanvasInputComponent in the canvas hierarchy
            var canvas = UITransform.GetCanvasComponent();
            _subscribedInput = canvas?.SceneNode?.GetComponent<UICanvasInputComponent>();

            if (_subscribedInput is not null)
            {
                _subscribedInput.EscapePressed += OnEscapeDismiss;
                _subscribedInput.RightClick += OnOutsideRightClick;
                _subscribedInput.LeftClickDown += OnLeftClickDown;
            }

            // Delay one frame before listening for left-clicks so the same click that
            // opened the menu doesn't immediately dismiss it.
            _dismissClickArmed = false;
            Engine.Time.Timer.PostUpdateFrame += ArmDismissClick;
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
            Engine.Time.Timer.PostUpdateFrame -= ArmDismissClick;
        }

        /// <summary>
        /// One-shot handler that arms outside-click detection on the frame after the menu opens.
        /// </summary>
        private void ArmDismissClick()
        {
            Engine.Time.Timer.PostUpdateFrame -= ArmDismissClick;
            if (!_isOpen)
                return;
            _dismissClickArmed = true;
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

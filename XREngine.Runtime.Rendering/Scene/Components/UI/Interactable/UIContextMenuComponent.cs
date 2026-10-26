using System.Numerics;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;
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

        /// <summary>
        /// Items of the submenu that this item opens. The list is empty for an action item.
        /// </summary>
        public IReadOnlyList<ContextMenuItem> SubmenuItems => _submenuItems;

        /// <summary>True if this item opens a submenu instead of running an action.</summary>
        public bool HasSubmenu => _submenuItems.Length > 0;

        private readonly ContextMenuItem[] _submenuItems = [];

        public ContextMenuItem(string label, Action? action, bool enabled = true)
        {
            Label = label;
            Action = action;
            Enabled = enabled;
            IsSeparator = false;
        }

        private ContextMenuItem(string label, ContextMenuItem[] submenuItems, bool enabled)
        {
            Label = label;
            Action = null;
            Enabled = enabled;
            IsSeparator = false;
            _submenuItems = submenuItems;
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

        /// <summary>
        /// Creates an item that opens a submenu. The item keeps a copy of
        /// <paramref name="items"/>, so later changes to the caller's array have no effect.
        /// </summary>
        public static ContextMenuItem Submenu(string label, ContextMenuItem[] items, bool enabled = true)
        {
            ArgumentNullException.ThrowIfNull(items);
            if (items.Length == 0)
                throw new ArgumentException("A submenu needs at least one item.", nameof(items));
            for (int index = 0; index < items.Length; index++)
                ArgumentNullException.ThrowIfNull(items[index], nameof(items));
            return new ContextMenuItem(label, (ContextMenuItem[])items.Clone(), enabled);
        }
    }

    /// <summary>
    /// A native UI context menu that can be shown at an arbitrary canvas position.
    /// Renders as a vertical list of <see cref="UIButtonComponent"/> items.
    /// Auto-dismisses on item click, click outside, or Escape key.
    /// </summary>
    [RequiresTransform(typeof(UIBoundableTransform))]
    public partial class UIContextMenuComponent : UIComponent
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
        private ObjectConstructionIdentity _showIdentity;
        private ObjectConstructionIdentity _showNodeIdentity;
        private ObjectConstructionIdentity _showTransformIdentity;
        private UIContextMenuComponent? _ownerMenu;
        private ulong _ownerShowRevision;
        private UIContextMenuComponent? _submenu;
        private UIContextMenuLeafConstruction? _submenuConstruction;
        private ulong _submenuShowRevision;
        private UIContextMenuItemComponent? _submenuRow;
        private bool _openingSubmenu;
        private SceneNode? _contentRoot;
        private ObjectConstructionIdentity _contentRootIdentity;
        private ObjectConstructionIdentity _contentTransformIdentity;
        private ObjectConstructionIdentity _contentParentIdentity;

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

        /// <summary>Gets the menu that opened this menu as a submenu, or null for a root menu.</summary>
        public UIContextMenuComponent? OwnerMenu => _ownerMenu;

        /// <summary>Gets the open submenu of this menu, or null.</summary>
        public UIContextMenuComponent? OpenSubmenu => _submenu is { _isOpen: true } submenu ? submenu : null;

        private void ShowCore(Vector2 parentLocalPosition, ContextMenuItem[] items, IVirtualListProvider? provider,
            Action<SceneNode>? contentBuilder = null, float contentHeight = 0, bool passive = false,
            bool subscribeForDismiss = true)
        {
            if (_tearingDown || IsDestroyed || IsDestroyQueued)
                return;
            ArgumentNullException.ThrowIfNull(items);
            for (int index = 0; index < items.Length; index++)
                ArgumentNullException.ThrowIfNull(items[index], nameof(items));
            RequestTransition(new(true, true, parentLocalPosition,
                items.Length == 0 ? [] : (ContextMenuItem[])items.Clone(), provider,
                contentBuilder, contentHeight, passive, subscribeForDismiss, ItemHeight, SeparatorHeight));
        }

        private void ExecuteShow(UIContextMenuRequest request)
        {
            Vector2 parentLocalPosition = request.Position;
            ContextMenuItem[] items = request.Items!;
            IVirtualListProvider? provider = request.Provider;
            Action<SceneNode>? contentBuilder = request.ContentBuilder;
            float contentHeight = request.ContentHeight;
            bool passive = request.Passive;
            bool subscribeForDismiss = request.SubscribeForDismiss;
            ulong showRevision = ++_showRevision;
            _showIdentity = ObjectConstructionIdentity.Capture(this);
            _showNodeIdentity = ObjectConstructionIdentity.Capture(SceneNode);
            _showTransformIdentity = ObjectConstructionIdentity.Capture(Transform);
            _ = BoundableTransform.GetCanvasTransform() ??
                throw new InvalidOperationException("A context menu needs an attached UI canvas.");
            float itemHeight = request.ItemHeight;
            float separatorHeight = request.SeparatorHeight;
            HideNow(clearMenuItems: true);
            if (_pendingTransition is not null || !HasCurrentShowLifetime(showRevision))
                return;
            ClearMenuItems(); // Remove content retained when the owner deactivated.
            if (_pendingTransition is not null || !HasCurrentShowLifetime(showRevision))
                return;

            if ((items.Length == 0 && provider is null && contentBuilder is null) || provider is { Count: <= 0 })
                return;
            if (_pendingTransition is not null || !HasCurrentShowLifetime(showRevision))
                return;

            _items = items;
            _itemHeight = itemHeight;
            _separatorHeight = separatorHeight;
            _virtualProvider = provider;
            _contentBuilder = contentBuilder;
            _contentHeight = contentHeight;
            IsOpen = true;
            if (!CanContinueConstruction(showRevision))
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
            if (!CanContinueConstruction(showRevision))
                return;

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
            if (!CanContinueConstruction(showRevision))
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
                bgMat = BuildMenuComponent(SceneNode, showRevision, () => new UIMaterialComponent(),
                    (background, write) =>
                    {
                        write(() => background.SetBatchSolidColor(BackgroundColor));
                        write(() => background.RenderCommand2D.ZIndex = BackgroundZIndex);
                        write(() => background.ClipToBounds = true);
                    });
                if (bgMat is null) return;
            }
            else
            {
                if (_incompleteComponentCreations.Contains(bgMat))
                    throw new InvalidOperationException("The menu backdrop has an incomplete creation receipt.");
                bgMat.SetQuadColor("MatColor", BackgroundColor);
                bgMat.RenderCommand2D.ZIndex = BackgroundZIndex;
                bgMat.ClipToBounds = true;
            }

            BuildMenuItems(showRevision);
            if (!CanContinueConstruction(showRevision))
                return;
            tfm.InvalidateLayout();
            if (!ReferenceEquals(tfm.GetCanvasTransform(), _placementCanvas))
            {
                Hide();
                return;
            }
            canvas.UpdateLayoutAsync();
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, CompletePlacement);
            // The root menu handles dismissal for its whole submenu chain.
            if (!passive && subscribeForDismiss)
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
            if (!_isOpen && !_transitioning)
                return;
            RequestTransition(new(false, clearMenuItems, default));
        }

        private void HideNow(bool clearMenuItems)
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
                CancelNavigationFocus();
                UnsubscribeForDismiss();
                // Deactivation keeps nodes; destroying them can change a hierarchy in teardown.
                CloseSubmenu(refocusRow: false, destroyNode: clearMenuItems);
                if (_tearingDown || IsDestroyed)
                    return;
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
                _ownerMenu = null;
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

        private void BuildMenuItems(ulong showRevision)
        {
            if (!CanContinueConstruction(showRevision))
                return;
            bool ordinaryItems = _contentBuilder is null && _virtualProvider is null;
            SceneNode? listNode;
            UIListTransform listTfm;
            if (ordinaryItems)
            {
                listNode = BuildOrdinaryMenuList(showRevision);
                if (listNode is null || !CanContinueConstruction(showRevision))
                    return;
                listTfm = (UIListTransform)listNode.Transform;
            }
            else
            {
                // Provider and custom builders can create their own children. They keep
                // the existing attached-content contract, not detached-leaf retirement.
                listNode = CreateContentRoot(new UIListTransform(), showRevision);
                if (!CanContinueConstruction(showRevision))
                    return;
                listNode.Name = "ContextMenuList";
                listTfm = (UIListTransform)listNode.Transform;
                listTfm.DisplayHorizontal = false;
                listTfm.ItemSpacing = 0;
                listTfm.Padding = Vector4.Zero;
                listTfm.Margins = Vector4.Zero;
                listTfm.MinAnchor = Vector2.Zero;
                listTfm.MaxAnchor = Vector2.One;
                listTfm.Translation = Vector2.Zero;
                listTfm.Width = null;
                listTfm.Height = null;
                listTfm.ItemAlignment = EListAlignment.TopOrLeft;
            }
            if (!CanContinueConstruction(showRevision))
                return;

            if (_contentBuilder is not null)
            {
                _contentBuilder(listNode);
                return;
            }

            if (_virtualProvider is not null)
            {
                BuildVirtualMenuScroll(listNode, listTfm, showRevision);
                return;
            }

            float contentTop = 0;
            foreach (var item in _items)
            {
                if (!CanContinueConstruction(showRevision))
                    return;
                if (item.IsSeparator)
                {
                    BuildSeparator(listNode, showRevision);
                    contentTop += _separatorHeight;
                }
                else
                {
                    BuildMenuItem(listNode, item, showRevision, contentTop);
                    contentTop += _itemHeight;
                }
            }
        }

        private readonly List<XREngine.Scene.Prefabs.DetachedLeafRetirementReceipt> _failedConstructionLeaves = [];

        internal IReadOnlyList<XREngine.Scene.Prefabs.DetachedLeafRetirementReceipt> FailedConstructionLeaves
            => _failedConstructionLeaves;

        private SceneNode? BuildMenuLeaf(SceneNode parent, ulong showRevision,
            Action<UIContextMenuLeafConstruction> configure, Func<bool>? ownsSource = null,
            UIBoundableTransform? transform = null, string name = "ContextMenuItem", bool contentRoot = false)
        {
            InvalidOperationException? cancellation = null;
            void RequireShow()
            {
                if (!IsCurrentShow(showRevision) ||
                    (contentRoot ? !ReferenceEquals(parent, SceneNode) : !OwnsConstructionParent(parent)) ||
                    ownsSource is not null && !ownsSource())
                    throw new InvalidOperationException("The menu construction owner changed.");
                if (_pendingTransition is not null)
                    throw cancellation ??= new InvalidOperationException("Menu construction was superseded.");
            }
            RequireShow();
            UIContextMenuLeafConstruction leaf = new(parent, RequireShow, transform, name);
            try
            {
                configure(leaf);
                if (contentRoot) CaptureContentRoot(leaf.Node);
                leaf.Attach();
                return leaf.Node;
            }
            catch (Exception failure)
            {
                bool cleanupHandled = leaf.RetireFailedLeaf();
                if (contentRoot && ReferenceEquals(_contentRoot, leaf.Node) && !OwnsContentRoot(leaf.Node))
                {
                    _contentRoot = null;
                    _contentRootIdentity = default;
                    _contentTransformIdentity = default;
                    _contentParentIdentity = default;
                }
                if (!cleanupHandled)
                {
                    _failedConstructionLeaves.Add(leaf.Retirement);
                    if (ReferenceEquals(failure, cancellation))
                        throw new InvalidOperationException("Cancelled menu content could not be retired.",
                            leaf.Retirement.Failure);
                }
                if (ReferenceEquals(failure, cancellation))
                    return null;
                throw;
            }
        }

        private bool OwnsConstructionParent(SceneNode parent)
        {
            if (_contentRoot is not { } root || !OwnsContentRoot(root) ||
                root.IsDestroyed || root.IsDestroyQueued ||
                root.Transform.IsDestroyed || root.Transform.IsDestroyQueued)
                return false;
            for (SceneNode? node = parent; node is not null; node = node.Parent)
                if (ReferenceEquals(node, root)) return true;
            return false;
        }

        private void BuildMenuItem(SceneNode parent, ContextMenuItem item, ulong showRevision, float contentTop)
        {
            UIContextMenuItemComponent button = null!;
            SceneNode? buttonNode = BuildMenuLeaf(parent, showRevision, leaf =>
            {
                button = leaf.Add(() => new UIContextMenuItemComponent());
                UIMaterialComponent background = leaf.Add(() => new UIMaterialComponent());
                leaf.Write(() => background.SetBatchSolidColor(ColorF4.Transparent));
                leaf.Write(() => background.RenderCommand2D.ZIndex = ItemBackgroundZIndex);
                leaf.Write(() => leaf.Transform.MinAnchor = Vector2.Zero);
                leaf.Write(() => leaf.Transform.MaxAnchor = new Vector2(1, 0));
                leaf.Write(() => leaf.Transform.Height = _itemHeight);
                leaf.Write(() => leaf.Transform.Margins = Vector4.Zero);
                leaf.Write(() => button.IsEnabled = item.Enabled);
                leaf.Write(() => button.IsTabStop = item.Enabled);
                leaf.Write(() => button.DefaultBackgroundColor = ColorF4.Transparent);
                leaf.Write(() => button.HighlightBackgroundColor = item.Enabled ? ItemHighlightBackground : ColorF4.Transparent);
                leaf.Write(() => button.DefaultTextColor = item.Enabled ? ItemDefaultTextColor : ItemDisabledTextColor);
                leaf.Write(() => button.HighlightTextColor = item.Enabled ? ItemDefaultTextColor : ItemDisabledTextColor);
            });
            if (buttonNode is null) return;
            button.Bind(this, showRevision, item, contentTop, contentTop + _itemHeight);
            if (!IsCurrentItem(button, showRevision)) return;
            ColorF4 textColor = item.Enabled ? ItemDefaultTextColor : ItemDisabledTextColor;
            if (BuildMenuText(buttonNode, item.Label, EHorizontalAlignment.Left, textColor, showRevision, () => IsCurrentItem(button, showRevision)) is null)
                return;
            if (!IsCurrentItem(button, showRevision) || !CanContinueConstruction(showRevision)) return;
            if (item.HasSubmenu &&
                BuildMenuText(buttonNode, ">", EHorizontalAlignment.Right, textColor, showRevision, () => IsCurrentItem(button, showRevision)) is null)
                return;
            if (!IsCurrentItem(button, showRevision) || !CanContinueConstruction(showRevision)) return;
            if (item.Enabled)
            {
                _itemButtons.Add(button);
                button.RegisterClickActions(_ =>
                {
                    if (!IsCurrentItem(button, showRevision) || !button.CanInteract) return;
                    if (item.HasSubmenu)
                    {
                        OpenItemSubmenu(button, showRevision, focusFirstItem: false);
                        return;
                    }
                    item.Action?.Invoke();
                    if (IsCurrentShow(showRevision)) HideMenuChain();
                });
            }
        }

        private SceneNode? BuildMenuText(SceneNode parent, string text, EHorizontalAlignment alignment,
            ColorF4 color, ulong showRevision, Func<bool> ownsSource)
            => BuildMenuLeaf(parent, showRevision, leaf =>
            {
                UITextComponent label = leaf.Add(() => new UITextComponent());
                leaf.Write(() => label.Text = text);
                leaf.Write(() => label.FontSize = FontSize);
                leaf.Write(() => label.Color = color);
                leaf.Write(() => label.HorizontalAlignment = alignment);
                leaf.Write(() => label.VerticalAlignment = EVerticalAlignment.Center);
                leaf.Write(() => leaf.Transform.Margins = new Vector4(ItemPadding, 0, ItemPadding, 0));
                leaf.Write(() => label.RenderCommand2D.ZIndex = LabelZIndex);
            }, ownsSource);

        private void BuildSeparator(SceneNode parent, ulong showRevision)
        {
            SceneNode? separator = BuildMenuLeaf(parent, showRevision, leaf =>
            {
                leaf.Add(() => new UIMaterialComponent());
                leaf.Write(() => leaf.Transform.MinAnchor = Vector2.Zero);
                leaf.Write(() => leaf.Transform.MaxAnchor = new Vector2(1, 0));
                leaf.Write(() => leaf.Transform.Height = _separatorHeight);
                leaf.Write(() => leaf.Transform.Margins = Vector4.Zero);
            });
            if (separator is null || !CanContinueConstruction(showRevision)) return;
            BuildMenuLeaf(separator, showRevision, leaf =>
            {
                UIMaterialComponent line = leaf.Add(() => new UIMaterialComponent());
                leaf.Write(() => leaf.Transform.MinAnchor = new Vector2(0, 0.5f));
                leaf.Write(() => leaf.Transform.MaxAnchor = new Vector2(1, 0.5f));
                leaf.Write(() => leaf.Transform.Height = 1);
                leaf.Write(() => leaf.Transform.Margins = new Vector4(ItemPadding, 0, ItemPadding, 0));
                leaf.Write(() => line.SetBatchSolidColor(SeparatorColor));
                leaf.Write(() => line.RenderCommand2D.ZIndex = SeparatorZIndex);
            });
        }

        private void ClearMenuItems()
        {
            if (_clearingMenuItems || _tearingDown || IsDestroyed)
                return;
            _clearingMenuItems = true;
            ReleaseItemButtons();
            try
            {
                SceneNode? root = _contentRoot;
                bool owned = root is not null && OwnsContentRoot(root);
                _contentRoot = null;
                _contentRootIdentity = default;
                _contentTransformIdentity = default;
                _contentParentIdentity = default;
                // Only this menu's captured content belongs to this operation. A callback
                // can move it or add another owner's child while the menu is open.
                if (owned && root is { IsDestroyed: false })
                    root.Destroy(true);
            }
            finally
            {
                _clearingMenuItems = false;
            }
        }

        private bool OwnsContentRoot(SceneNode root)
            => ReferenceEquals(root, _contentRoot) && _contentRootIdentity.IsCurrent &&
                _contentTransformIdentity.IsCurrent && _contentParentIdentity.IsCurrent &&
                ReferenceEquals(root.Transform, _contentTransformIdentity.Target) &&
                ReferenceEquals(root.Transform.Parent, _contentParentIdentity.Target) &&
                ReferenceEquals(Transform, _contentParentIdentity.Target);

        private void CaptureContentRoot(SceneNode root)
        {
            _contentRoot = root;
            _contentRootIdentity = ObjectConstructionIdentity.Capture(root);
            _contentTransformIdentity = ObjectConstructionIdentity.Capture(root.Transform);
            _contentParentIdentity = ObjectConstructionIdentity.Capture(Transform);
        }

        private SceneNode CreateContentRoot(UIBoundableTransform transform, ulong showRevision)
        {
            SceneNode root = new("ContextMenuContent", transform);
            CaptureContentRoot(root);
            transform.SetParent(Transform, false, EParentAssignmentMode.Immediate);
            // Attachment publishes callbacks before the builder can configure this root.
            // Continue only while the captured root still belongs to this show.
            if (!IsCurrentShow(showRevision) || !OwnsContentRoot(root) ||
                root.IsDestroyed || root.IsDestroyQueued || transform.IsDestroyed || transform.IsDestroyQueued)
                throw new InvalidOperationException("Context menu content changed ownership during attachment.");
            return root;
        }

        private bool HasCurrentShowLifetime(ulong showRevision)
            => !_tearingDown && !IsDestroyed && !IsDestroyQueued && showRevision == _showRevision &&
                _showIdentity.Target is not null && _showIdentity.IsCurrent &&
                _showNodeIdentity.IsCurrent && _showTransformIdentity.IsCurrent &&
                ReferenceEquals(SceneNode, _showNodeIdentity.Target) &&
                ReferenceEquals(Transform, _showTransformIdentity.Target) &&
                !SceneNode.IsDestroyed && !SceneNode.IsDestroyQueued &&
                !Transform.IsDestroyed && !Transform.IsDestroyQueued;

        private bool IsCurrentShow(ulong showRevision)
            => _isOpen && HasCurrentShowLifetime(showRevision) &&
                (_ownerMenu is null ||
                    (ReferenceEquals(_ownerMenu._submenu, this) && _ownerMenu._submenuRow is { } row &&
                        _ownerMenu._submenuConstruction?.OwnsAttachedNode() == true &&
                        _ownerMenu._submenuShowRevision == showRevision &&
                        _ownerMenu.IsCurrentItem(row, _ownerShowRevision)));

        // A newer request cancels construction at its next boundary. Keep it queued
        // for the transition loop; cancellation is not an ownership failure.
        private bool CanContinueConstruction(ulong showRevision)
            => _pendingTransition is null && IsCurrentShow(showRevision);

        private bool IsCurrentItem(UIContextMenuItemComponent row, ulong showRevision)
            => IsCurrentShow(showRevision) && row.HasCurrentBinding(this, showRevision) &&
                IsDescendantOfSelf(row.SceneNode);

        /// <summary>
        /// Requests focus for an enabled row. Scrolls to that row and waits for presented
        /// focus when necessary. Returns false when the source or request is no longer valid.
        /// </summary>
        internal bool MoveItemFocus(UIContextMenuItemComponent from, ulong showRevision,
            int direction, bool toEdge)
        {
            if (direction == 0 || !IsCurrentItem(from, showRevision))
                return false;
            int count = _itemButtons.Count;
            int index = _itemButtons.IndexOf(from);
            if (index < 0 || UITransform.GetCanvasComponent() is not { } canvas ||
                canvas.GetInputComponent() is not { } input)
                return false;
            bool hasPending = _navigationTickRegistered && ReferenceEquals(_navigationSource, from) &&
                IsCurrentNavigationContext(_navigationSerial) && CanWaitForNavigationFocus();
            if (hasPending)
                index = _itemButtons.IndexOf(_navigationTarget!);
            else
                CancelNavigationFocus();
            if (index < 0)
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
                if (ReferenceEquals(target, from) && !hasPending)
                    return false;
                if (!IsCurrentItem(target, showRevision) || !target.CanInteract)
                    continue;
                // A clipped row remains the intended target. Do not skip it for a visible row.
                return RequestItemFocus(from, target, showRevision, canvas, input);
            }
            return false;
        }

        /// <summary>
        /// Opens the submenu of a row of the current show. The submenu is a sibling node of
        /// this menu, so it is not clipped by this menu. It opens to the right of the row, or
        /// to the left when the right side has no room. Returns false when the row belongs to
        /// an earlier show, has no submenu, or a callback closed or replaced a menu.
        /// </summary>
        internal bool OpenItemSubmenu(UIContextMenuItemComponent row, ulong showRevision, bool focusFirstItem)
        {
            if (_openingSubmenu || !IsCurrentItem(row, showRevision) ||
                row.Item is not { HasSubmenu: true, Enabled: true } item)
                return false;
            if (_submenu is { _isOpen: true } current && ReferenceEquals(_submenuRow, row) &&
                _submenuConstruction?.OwnsAttachedNode() == true &&
                ReferenceEquals(current._ownerMenu, this) && current._showRevision == _submenuShowRevision)
            {
                UIContextMenuLeafConstruction construction = _submenuConstruction!;
                ulong expectedShowRevision = _submenuShowRevision;
                if (focusFirstItem)
                    current.RequestFirstItemFocus(row);
                else
                    current.CancelNavigationFocus();
                if (IsCurrentItem(row, showRevision) && _pendingTransition is null &&
                    ReferenceEquals(_submenu, current) && ReferenceEquals(_submenuConstruction, construction) &&
                    construction.OwnsAttachedNode() && ReferenceEquals(current._ownerMenu, this) &&
                    current.IsCurrentShow(expectedShowRevision))
                    return true;
                CloseConstructedSubmenu(construction, current, expectedShowRevision);
                return false;
            }
            _openingSubmenu = true;
            List<UIContextMenuComponent> heldTransitions = HoldSubmenuTransitions();
            UIContextMenuComponent? opened = null;
            ulong openedRevision = 0;
            Exception? openingFailure = null;
            try
            {
                CloseSubmenu(refocusRow: false);
                if (IsCurrentItem(row, showRevision) && !HasPendingSubmenuTransition() &&
                    SceneNode.Parent is { } host &&
                    BuildItemSubmenu(host, row, showRevision, item, focusFirstItem))
                {
                    opened = _submenu;
                    openedRevision = _submenuShowRevision;
                }
            }
            catch (Exception failure)
            {
                openingFailure = failure;
                throw;
            }
            finally
            {
                try { ReleaseSubmenuTransitions(heldTransitions); }
                catch (Exception drainFailure) when (openingFailure is not null)
                {
                    throw new AggregateException("Submenu construction and queued transition failed.",
                        openingFailure, drainFailure);
                }
                finally { _openingSubmenu = false; }
            }
            return opened is not null && ReferenceEquals(_submenu, opened) &&
                IsCurrentItem(row, showRevision) && ReferenceEquals(opened._ownerMenu, this) &&
                opened.IsCurrentShow(openedRevision);
        }

        /// <summary>Closes this submenu from one of its rows and returns focus to the owner row.</summary>
        internal bool CloseFromItem(UIContextMenuItemComponent row, ulong showRevision)
        {
            if (!IsCurrentItem(row, showRevision) || _ownerMenu is not { } owner ||
                !ReferenceEquals(owner._submenu, this) || !owner.IsCurrentShow(_ownerShowRevision) ||
                owner._submenuRow is not { } ownerRow || !owner.IsCurrentItem(ownerRow, _ownerShowRevision))
                return false;
            owner.CloseSubmenu(refocusRow: true);
            return true;
        }

        private Vector2 ResolveSubmenuPosition(ContextMenuItem item, float submenuWidth)
        {
            UIBoundableTransform transform = BoundableTransform;
            float offset = 0;
            foreach (ContextMenuItem candidate in _items)
            {
                if (ReferenceEquals(candidate, item))
                    break;
                offset += candidate.IsSeparator ? _separatorHeight : _itemHeight;
            }
            Vector2 topLeft = transform.Translation;
            float x = topLeft.X + PreferredWidth;
            if (transform.Parent is UIBoundableTransform parent && parent.ActualWidth > 0 &&
                x + submenuWidth > parent.ActualWidth)
                x = topLeft.X - submenuWidth;
            return new Vector2(x, topLeft.Y - offset);
        }

        /// <summary>
        /// Hides the open submenu. When <paramref name="destroyNode"/> is false, the hidden
        /// submenu node stays attached and the next submenu open or hide destroys it.
        /// </summary>
        private void CloseSubmenu(bool refocusRow, bool destroyNode = true)
        {
            UIContextMenuLeafConstruction? construction = _submenuConstruction;
            ulong submenuShowRevision = _submenuShowRevision;
            UIContextMenuItemComponent? row = _submenuRow;
            _submenu = null;
            _submenuRow = null;
            if (destroyNode)
                _submenuConstruction = null;
            // Opening admission can expire before cleanup. Use the captured node and
            // component ownership, not the old row binding, to release this submenu.
            if (construction is not null && construction.OwnsAttachedNode())
            {
                UIContextMenuComponent submenu = construction.Node.GetComponent<UIContextMenuComponent>()!;
                if (submenu._showRevision != submenuShowRevision)
                    return;
                if (ReferenceEquals(submenu._ownerMenu, this))
                    submenu._ownerMenu = null;
                if (destroyNode)
                    submenu.Hide();
                else
                    submenu.HideCore(clearMenuItems: false);
                // Hide callbacks can transfer this node or open a new independent show.
                if (destroyNode && construction.OwnsAttachedNode() && !submenu.IsOpen &&
                    submenu._showRevision == submenuShowRevision)
                    construction.Node.Destroy(true);
            }
            if (refocusRow && row is not null && IsCurrentItem(row, row.ShowRevision) &&
                UITransform.GetCanvasComponent()?.GetInputComponent() is { } input)
                input.TryFocusPresented(row);
        }

        /// <summary>Hides the root of this submenu chain, which also hides every open submenu.</summary>
        private void HideMenuChain()
        {
            UIContextMenuComponent root = this;
            while (root._ownerMenu is { } owner && owner.IsCurrentShow(root._ownerShowRevision) &&
                ReferenceEquals(owner._submenu, root))
                root = owner;
            root.Hide();
            if (!ReferenceEquals(root, this))
                Hide();
        }

        /// <summary>Closes the innermost open submenu, or this menu when no submenu is open.</summary>
        private void CloseInnermost()
        {
            if (_submenu is { _isOpen: true } submenu)
            {
                if (submenu._submenu is { _isOpen: true })
                    submenu.CloseInnermost();
                else
                    CloseSubmenu(refocusRow: true);
                return;
            }
            Hide();
        }

        private bool IsInsideMenuChain(SceneNode? candidate)
            => IsDescendantOfSelf(candidate) ||
                _submenu is { _isOpen: true } submenu && submenu.IsInsideMenuChain(candidate);

        private bool ContainsMenu(UIContextMenuComponent menu)
            => ReferenceEquals(menu, this) ||
                _submenu is { _isOpen: true } submenu && submenu.ContainsMenu(menu);

        /// <summary>Moves presented focus to the first enabled row of this menu.</summary>
        internal bool FocusFirstItem()
        {
            if (!IsCurrentShow(_showRevision) ||
                UITransform.GetCanvasComponent()?.GetInputComponent() is not { } input)
                return false;
            ulong showRevision = _showRevision;
            int count = _itemButtons.Count;
            for (int index = 0; index < count; index++)
            {
                UIContextMenuItemComponent target = _itemButtons[index];
                if (!IsCurrentItem(target, showRevision) || !target.CanInteract)
                    continue;
                if (input.TryFocusPresented(target))
                    return IsCurrentItem(target, showRevision);
                if (!IsCurrentShow(showRevision) || _itemButtons.Count != count)
                    return false;
            }
            return false;
        }

        private void ReleaseItemButtons()
        {
            CancelNavigationFocus();
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
            CloseInnermost();
        }

        private void OnGamepadBackPressed(UIInteractableComponent? focused)
        {
            if (!_isOpen || focused?.SceneNode is not { } focusedNode)
                return;
            // Only the closest open menu owns Back, including when menus are nested.
            // A root menu also handles Back for the submenus that it opened.
            for (SceneNode? node = focusedNode; node is not null; node = node.Parent)
                if (node.GetComponent<UIContextMenuComponent>() is { IsOpen: true } menu)
                {
                    if (ContainsMenu(menu))
                        CloseInnermost();
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

            // If clicked inside our menu subtree or an open submenu, the button handler acts.
            if (IsInsideMenuChain(clicked.SceneNode))
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
            _pendingTransition = null;
            _showRevision++;
            _awaitingPlacement = false;
            _isOpen = false;
            UnregisterTick(ETickGroup.Late, ETickOrder.Scene, CompletePlacement);
            CancelNavigationFocus();
            UnsubscribeForDismiss();
            CloseSubmenu(refocusRow: false);
            _ownerMenu = null;
            _items = [];
            _virtualProvider = null;
            _contentBuilder = null;
            _virtualList = null;
            _placementCanvas = null;
            _contentRoot = null;
            _contentRootIdentity = default;
            _contentTransformIdentity = default;
            _contentParentIdentity = default;
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

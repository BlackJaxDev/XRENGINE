using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// A row in a <see cref="UIContextMenuComponent"/>. On an enabled row, Up and Down move focus to
    /// the adjacent enabled row and wrap at the ends. Home and End move focus to the first
    /// and last enabled rows. Right opens the row's submenu and focuses its first row. Left
    /// closes the submenu that contains the row and returns focus to the row that opened it.
    /// A row acts only for the menu show that created it.
    /// </summary>
    public sealed class UIContextMenuItemComponent : UIButtonComponent
    {
        private readonly DelButtonState _upKey;
        private readonly DelButtonState _downKey;
        private readonly DelButtonState _homeKey;
        private readonly DelButtonState _endKey;
        private readonly DelButtonState _rightKey;
        private readonly DelButtonState _leftKey;
        private UIContextMenuComponent? _menu;
        private ContextMenuItem? _item;
        private ulong _showRevision;
        private ObjectConstructionIdentity _componentIdentity;
        private ObjectConstructionIdentity _nodeIdentity;
        private ObjectConstructionIdentity _transformIdentity;
        private ObjectConstructionIdentity _parentIdentity;
        private ObjectConstructionIdentity _parentTransformIdentity;

        public UIContextMenuItemComponent()
        {
            _upKey = pressed => Navigate(pressed, -1, toEdge: false);
            _downKey = pressed => Navigate(pressed, 1, toEdge: false);
            _homeKey = pressed => Navigate(pressed, -1, toEdge: true);
            _endKey = pressed => Navigate(pressed, 1, toEdge: true);
            _rightKey = OpenSubmenu;
            _leftKey = CloseSubmenu;
        }

        /// <summary>Gets the menu that created this row, or null after the row is released.</summary>
        public UIContextMenuComponent? Menu => _menu;

        internal ulong ShowRevision => _showRevision;

        /// <summary>Gets the item that this row shows, or null after the row is released.</summary>
        internal ContextMenuItem? Item => _menu is null ? null : _item;

        internal float ContentTop { get; private set; }
        internal float ContentBottom { get; private set; }

        internal void Bind(UIContextMenuComponent menu, ulong showRevision, ContextMenuItem item,
            float contentTop, float contentBottom)
        {
            SceneNode parent = SceneNode.Parent ??
                throw new InvalidOperationException("A menu row needs an attached parent.");
            _componentIdentity = ObjectConstructionIdentity.Capture(this);
            _nodeIdentity = ObjectConstructionIdentity.Capture(SceneNode);
            _transformIdentity = ObjectConstructionIdentity.Capture(Transform);
            _parentIdentity = ObjectConstructionIdentity.Capture(parent);
            _parentTransformIdentity = ObjectConstructionIdentity.Capture(parent.Transform);
            _menu = menu;
            _showRevision = showRevision;
            _item = item;
            ContentTop = contentTop;
            ContentBottom = contentBottom;
        }

        /// <summary>Checks the row's captured lifetime and attachment without invoking callbacks.</summary>
        internal bool HasCurrentBinding(UIContextMenuComponent menu, ulong showRevision)
            => ReferenceEquals(_menu, menu) && _showRevision == showRevision &&
                IsLive(_componentIdentity) && IsLive(_nodeIdentity) && IsLive(_transformIdentity) &&
                IsLive(_parentIdentity) && IsLive(_parentTransformIdentity) &&
                ReferenceEquals(SceneNode, _nodeIdentity.Target) &&
                ReferenceEquals(Transform, _transformIdentity.Target) &&
                ReferenceEquals(SceneNode.Parent, _parentIdentity.Target) &&
                ReferenceEquals(Transform.Parent, _parentTransformIdentity.Target) &&
                ReferenceEquals(((SceneNode)_parentIdentity.Target).Transform, _parentTransformIdentity.Target);

        private static bool IsLive(ObjectConstructionIdentity identity)
            => identity.Target is { IsDestroyed: false, IsDestroyQueued: false } && identity.IsCurrent;

        internal void Release()
        {
            _menu = null;
            _item = null;
            _componentIdentity = default;
            _nodeIdentity = default;
            _transformIdentity = default;
            _parentIdentity = default;
            _parentTransformIdentity = default;
        }

        public override void RegisterInput(IInputRegistration input)
        {
            base.RegisterInput(input);
            input.RegisterKeyStateChange(EKey.Up, _upKey);
            input.RegisterKeyStateChange(EKey.Down, _downKey);
            input.RegisterKeyStateChange(EKey.Home, _homeKey);
            input.RegisterKeyStateChange(EKey.End, _endKey);
            input.RegisterKeyStateChange(EKey.Right, _rightKey);
            input.RegisterKeyStateChange(EKey.Left, _leftKey);
        }

        private void Navigate(bool pressed, int direction, bool toEdge)
        {
            if (pressed && HasKeyboardFocus && CanInteract && _menu is { } menu)
                menu.MoveItemFocus(this, _showRevision, direction, toEdge);
        }

        private void OpenSubmenu(bool pressed)
        {
            if (pressed && HasKeyboardFocus && CanInteract && _menu is { } menu && _item is { HasSubmenu: true })
                menu.OpenItemSubmenu(this, _showRevision, focusFirstItem: true);
        }

        private void CloseSubmenu(bool pressed)
        {
            if (pressed && HasKeyboardFocus && _menu is { } menu)
                menu.CloseFromItem(this, _showRevision);
        }

        private bool HasKeyboardFocus => IsFocused &&
            ReferenceEquals(UserInterfaceCanvas?.GetInputComponent()?.FocusedComponent, this);

        protected override void OnDestroying()
        {
            Release();
            base.OnDestroying();
        }
    }
}

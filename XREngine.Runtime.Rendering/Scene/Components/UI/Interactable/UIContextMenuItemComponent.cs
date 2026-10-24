using XREngine.Input;
using XREngine.Input.Devices;

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

        internal void Bind(UIContextMenuComponent menu, ulong showRevision, ContextMenuItem item)
        {
            _menu = menu;
            _showRevision = showRevision;
            _item = item;
        }

        internal void Release()
        {
            _menu = null;
            _item = null;
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
            if (pressed && IsFocused && CanInteract && _menu is { } menu)
                menu.MoveItemFocus(this, _showRevision, direction, toEdge);
        }

        private void OpenSubmenu(bool pressed)
        {
            if (pressed && IsFocused && CanInteract && _menu is { } menu && _item is { HasSubmenu: true })
                menu.OpenItemSubmenu(this, _showRevision, focusFirstItem: true);
        }

        private void CloseSubmenu(bool pressed)
        {
            if (pressed && IsFocused && _menu is { } menu)
                menu.CloseFromItem(this, _showRevision);
        }

        protected override void OnDestroying()
        {
            _menu = null;
            _item = null;
            base.OnDestroying();
        }
    }
}

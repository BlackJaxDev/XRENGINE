using XREngine.Input;
using XREngine.Input.Devices;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// A row in a <see cref="UIContextMenuComponent"/>. On an enabled row, Up and Down move focus to
    /// the adjacent enabled row and wrap at the ends. Home and End move focus to the first
    /// and last enabled rows. A row acts only for the menu show that created it.
    /// </summary>
    public sealed class UIContextMenuItemComponent : UIButtonComponent
    {
        private readonly DelButtonState _upKey;
        private readonly DelButtonState _downKey;
        private readonly DelButtonState _homeKey;
        private readonly DelButtonState _endKey;
        private UIContextMenuComponent? _menu;
        private ulong _showRevision;

        public UIContextMenuItemComponent()
        {
            _upKey = pressed => Navigate(pressed, -1, toEdge: false);
            _downKey = pressed => Navigate(pressed, 1, toEdge: false);
            _homeKey = pressed => Navigate(pressed, -1, toEdge: true);
            _endKey = pressed => Navigate(pressed, 1, toEdge: true);
        }

        /// <summary>Gets the menu that created this row, or null after the row is released.</summary>
        public UIContextMenuComponent? Menu => _menu;

        internal ulong ShowRevision => _showRevision;

        internal void Bind(UIContextMenuComponent menu, ulong showRevision)
        {
            _menu = menu;
            _showRevision = showRevision;
        }

        internal void Release() => _menu = null;

        public override void RegisterInput(IInputRegistration input)
        {
            base.RegisterInput(input);
            input.RegisterKeyStateChange(EKey.Up, _upKey);
            input.RegisterKeyStateChange(EKey.Down, _downKey);
            input.RegisterKeyStateChange(EKey.Home, _homeKey);
            input.RegisterKeyStateChange(EKey.End, _endKey);
        }

        private void Navigate(bool pressed, int direction, bool toEdge)
        {
            if (pressed && IsFocused && CanInteract && _menu is { } menu)
                menu.MoveItemFocus(this, _showRevision, direction, toEdge);
        }

        protected override void OnDestroying()
        {
            _menu = null;
            base.OnDestroying();
        }
    }
}

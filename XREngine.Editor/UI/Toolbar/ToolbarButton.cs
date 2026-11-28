using Silk.NET.Input;
using XREngine.Data.Core;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI.Toolbar;

public class ToolbarButton : ToolbarItemBase
{
    private string _text = string.Empty;
    private Action<UIInteractableComponent>? _action;
    private bool _childOptionsVisible = false;
    private Key[]? _shortcutKeys;

    private ToolbarButton()
    {
        ChildOptions.PostAnythingAdded += ChildOptions_PostAnythingAdded;
        ChildOptions.PostAnythingRemoved += ChildOptions_PostAnythingRemoved;
    }
    public ToolbarButton(string? text, params ToolbarButton[] childOptions) : this()
    {
        Text = text ?? string.Empty;
        ChildOptions.AddRange(childOptions);
    }
    public ToolbarButton(string? text, Action<UIInteractableComponent>? action, Key[]? shortcutKeys = null) : this()
    {
        Text = text ?? string.Empty;
        Action = action;
        ShortcutKeys = shortcutKeys;
    }
    public ToolbarButton(string? text, Key[]? shortcutKeys, params ToolbarButton[] childOptions) : this()
    {
        Text = text ?? string.Empty;
        ShortcutKeys = shortcutKeys;
        ChildOptions.AddRange(childOptions);
    }
    public ToolbarButton(
        string? text,
        Action<UIInteractableComponent>? action,
        Key[]? shortcutKeys,
        params ToolbarButton[] childOptions) : this()
    {
        Text = text ?? string.Empty;
        Action = action;
        ShortcutKeys = shortcutKeys;
        ChildOptions.AddRange(childOptions);
    }

    private void ChildOptions_PostAnythingRemoved(ToolbarItemBase item)
        => item.Parent = null;
    private void ChildOptions_PostAnythingAdded(ToolbarItemBase item)
        => item.Parent = this;

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }
    public Action<UIInteractableComponent>? Action
    {
        get => _action;
        set => SetField(ref _action, value);
    }

    public EventList<ToolbarItemBase> ChildOptions { get; } = [];

    public bool ChildOptionsVisible
    {
        get => _childOptionsVisible;
        set => SetField(ref _childOptionsVisible, value);
    }
    public Key[]? ShortcutKeys
    {
        get => _shortcutKeys;
        set => SetField(ref _shortcutKeys, value);
    }

    /// <summary>
    /// Tells whether the item can run now, such as Undo when there is an undo step. The menu reads it each time
    /// the parent menu opens and draws a disabled item dimmed. Null means always enabled.
    /// </summary>
    public Func<bool>? IsEnabledProvider { get; init; }

    /// <summary>
    /// Tells whether the item shows a check mark, such as a panel that is open. The menu reads it each time the
    /// parent menu opens. Null means the item has no check mark.
    /// </summary>
    public Func<bool>? IsCheckedProvider { get; init; }

    /// <summary>The shortcut text that a drop-down menu shows at the right of the item, such as "Ctrl+Shift+S".</summary>
    public string? ShortcutText => FormatShortcut(ShortcutKeys);

    /// <summary>True when the item opens a submenu instead of running an action.</summary>
    public bool HasSubmenu => ChildOptions.Count > 0;

    /// <summary>The submenu list that the toolbar created for this item, or null for a leaf item.</summary>
    internal UIBoundableTransform? SubmenuTransform { get; set; }
    /// <summary>The label of the item in its menu. <see cref="RefreshMenuState"/> dims it when the item is disabled.</summary>
    internal UITextComponent? Label { get; set; }
    /// <summary>The check mark of the item, shown when <see cref="IsCheckedProvider"/> returns true.</summary>
    internal UIBoundableTransform? CheckMark { get; set; }

    /// <summary>Gets whether the item can run now.</summary>
    public bool IsEnabledNow => IsEnabledProvider?.Invoke() ?? true;

    /// <summary>Reads the enabled and checked state again and applies it to the item's controls.</summary>
    internal void RefreshMenuState()
    {
        bool enabled = IsEnabledNow;
        if (InteractableComponent is { } button && button.IsEnabled != enabled)
            button.IsEnabled = enabled;
        if (Label is { } label)
        {
            var color = enabled ? EditorGlassTheme.TextPrimary : EditorGlassTheme.TextDisabled;
            if (!label.Color.Equals(color))
                label.Color = color;
        }
        if (CheckMark is { } check)
        {
            EVisibility visibility = IsCheckedProvider?.Invoke() == true ? EVisibility.Visible : EVisibility.Hidden;
            if (check.Visibility != visibility)
                check.Visibility = visibility;
        }
    }

    /// <summary>Formats shortcut keys as text, such as "Ctrl+Shift+S". Returns null for no keys.</summary>
    public static string? FormatShortcut(Key[]? keys)
    {
        if (keys is not { Length: > 0 })
            return null;
        string[] parts = new string[keys.Length];
        for (int index = 0; index < keys.Length; index++)
            parts[index] = keys[index] switch
            {
                Key.ControlLeft or Key.ControlRight => "Ctrl",
                Key.ShiftLeft or Key.ShiftRight => "Shift",
                Key.AltLeft or Key.AltRight => "Alt",
                Key.SuperLeft or Key.SuperRight => "Win",
                >= Key.Number0 and <= Key.Number9 => ((int)(keys[index] - Key.Number0)).ToString(),
                Key key => key.ToString(),
            };
        return string.Join("+", parts);
    }

    public override void OnInteracted(UIInteractableComponent component)
    {
        if (!IsEnabledNow)
            return;
        Action?.Invoke(component);
        // A menu bar item toggles its menu. A drop-down item that has a submenu opens it, because the pointer has
        // usually opened it already on hover, and a click must not close it again.
        if (HasSubmenu)
            ChildOptionsVisible = Parent is not null || !ChildOptionsVisible;
        else
        {
            ChildOptionsVisible = false;
            if (InteractableComponent is not null)
                InteractableComponent.IsFocused = false;
            var parent = Parent;
            while (parent is not null)
            {
                parent.ChildOptionsVisible = false;
                parent = parent.Parent;
            }
        }
    }

    public override void OnCancelInteraction(UIInteractableComponent component)
        => ChildOptionsVisible = false;

    /// <summary>
    /// Opens this item's submenu when the pointer moves onto it while a menu around it is open, like ImGui. A
    /// drop-down item opens when its parent menu is open, and a menu bar item opens when another menu bar item
    /// is open.
    /// </summary>
    private void OpenOnHover()
    {
        if (!IsEnabledNow)
            return;
        bool menuOpen = Parent is { } parent
            ? parent.ChildOptionsVisible
            : ParentToolbarComponent?.ActiveSubmenus.Count > 0;
        if (!menuOpen)
            return;
        if (HasSubmenu)
            ChildOptionsVisible = true;
        else
            CloseSiblingMenus();
    }

    /// <summary>Closes the open submenus of the other items in the same menu, or the other menu bar menus.</summary>
    private void CloseSiblingMenus()
    {
        IEnumerable<ToolbarItemBase>? siblings = Parent is { } parent
            ? parent.ChildOptions
            : ParentToolbarComponent?.RootMenuOptions;
        if (siblings is null)
            return;
        foreach (ToolbarItemBase sibling in siblings.ToArray())
            if (!ReferenceEquals(sibling, this) && sibling is ToolbarButton { ChildOptionsVisible: true } open)
                open.ChildOptionsVisible = false;
    }

    public override bool AnyOptionsFocused => 
        base.AnyOptionsFocused ||
        ChildOptions.Any(c => c.AnyOptionsFocused);

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(ChildOptionsVisible):
                if (SubmenuTransform is not { } submenuTfm)
                    break;

                // Like ImGui, one submenu of a menu is open at a time, and a closing menu closes its submenus.
                if (ChildOptionsVisible)
                    CloseSiblingMenus();
                else
                    foreach (ToolbarItemBase child in ChildOptions)
                        if (child is ToolbarButton { ChildOptionsVisible: true } open)
                            open.ChildOptionsVisible = false;
                submenuTfm.Visibility = ChildOptionsVisible
                    ? EVisibility.Visible
                    : EVisibility.Collapsed;
                // The enabled and checked states can change while the menu is closed, so an opening menu reads them.
                // It reads them after the menu shows, because the layout does not take changes inside a collapsed
                // subtree.
                if (ChildOptionsVisible)
                    foreach (ToolbarItemBase child in ChildOptions)
                        if (child is ToolbarButton item)
                            item.RefreshMenuState();

                if (ParentToolbarComponent is null)
                    break;

                if (ChildOptionsVisible)
                    ParentToolbarComponent.ActiveSubmenus.Add(this);
                else
                    ParentToolbarComponent.ActiveSubmenus.Remove(this);

                break;
        }
    }

    /// <summary>
    /// Called when any of the interactable component's properties changes.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    protected override void OnInteractablePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
    {
        base.OnInteractablePropertyChanged(sender, e);
        switch (e.PropertyName)
        {
            case nameof(UIInteractableComponent.IsMouseOver):
                if (InteractableComponent?.IsMouseOver == true)
                    OpenOnHover();
                break;
        }
    }
}

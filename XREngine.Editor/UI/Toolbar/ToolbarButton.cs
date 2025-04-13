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

    public override void OnInteracted(UIInteractableComponent component)
    {
        Action?.Invoke(component);
        var interTfm = InteractableComponent?.Transform;
        if (interTfm?.ChildCount >= 2)
            ChildOptionsVisible = !ChildOptionsVisible;
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

    public override bool AnyOptionsFocused => 
        base.AnyOptionsFocused ||
        ChildOptions.Any(c => c.AnyOptionsFocused);

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(ChildOptionsVisible):

                var interTfm = InteractableComponent?.Transform;
                if (interTfm?.ChildCount < 2)
                    break;

                var submenuTfm = InteractableComponent?.Transform?.LastChild() as UIBoundableTransform;
                if (submenuTfm is not null)
                    submenuTfm.Visibility = ChildOptionsVisible
                        ? EVisibility.Visible
                        : EVisibility.Collapsed;

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
            case nameof(UIButtonComponent.IsFocused):
                if (!AnyOptionsFocused)
                    ChildOptionsVisible = false;
                break;
        }
    }
}
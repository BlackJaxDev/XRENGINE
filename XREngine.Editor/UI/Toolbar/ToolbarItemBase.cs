using XREngine.Data.Core;
using XREngine.Editor.UI.Components;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI.Toolbar;

public abstract class ToolbarItemBase : XRBase
{
    private ToolbarButton? _parent;
    public ToolbarButton? Parent
    {
        get => _parent;
        set => SetField(ref _parent, value);
    }

    private UIButtonComponent? _interactableComponent = null;
    /// <summary>
    /// The interactable component that represents this menu option.
    /// Upon setting this property, the interactable component will be subscribed to the appropriate events.
    /// </summary>
    public UIButtonComponent? InteractableComponent
    {
        get => _interactableComponent;
        set => SetField(ref _interactableComponent, value);
    }

    private UIToolbarComponent? _parentToolbarComponent = null;
    public UIToolbarComponent? ParentToolbarComponent
    {
        get => _parentToolbarComponent;
        set => SetField(ref _parentToolbarComponent, value);
    }

    public abstract void OnInteracted(UIInteractableComponent component);
    public abstract void OnCancelInteraction(UIInteractableComponent component);

    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool change = base.OnPropertyChanging(propName, field, @new);
        if (change)
        {
            switch (propName)
            {
                case nameof(InteractableComponent):
                    if (InteractableComponent is not null)
                    {
                        InteractableComponent.InteractAction -= OnInteracted;
                        InteractableComponent.BackAction -= OnCancelInteraction;
                        InteractableComponent.PropertyChanged -= OnInteractablePropertyChanged;
                    }
                    break;
            }
        }
        return change;
    }
    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(InteractableComponent):
                if (InteractableComponent is not null)
                {
                    InteractableComponent.InteractAction += OnInteracted;
                    InteractableComponent.BackAction += OnCancelInteraction;
                    InteractableComponent.PropertyChanged += OnInteractablePropertyChanged;
                }
                break;
        }
    }

    public virtual bool AnyOptionsFocused => InteractableComponent?.IsFocused ?? false;

    /// <summary>
    /// Called when any of the interactable component's properties changes.
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    protected virtual void OnInteractablePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UIButtonComponent.IsFocused):
                if (Parent is not null && !Parent.AnyOptionsFocused)
                    Parent.ChildOptionsVisible = false;
                break;
        }
    }
}

using System.Numerics;
using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// The actions button at the right of a component section header in the stacked inspector view. Like the "..."
/// button of an ImGui component header, it opens a menu that renames or removes the component, or inspects it
/// alone.
/// </summary>
public partial class InspectorPanel
{
    private NativePropertyPopupHost? _componentActionsHost;
    private UIContextMenuComponent? _componentActionsMenu;
    /// <summary>The rename field of the open rename popup. A late tick gives it the keyboard focus.</summary>
    private UITextInputComponent? _componentRenameInput;
    private int _componentRenameFocusAttempts;
    private const int MaxRenameFocusAttempts = 30;
    private const float ComponentActionsButtonWidth = 28.0f;

    /// <summary>
    /// Adds the host of the shared popup that the header actions buttons open. One popup serves all rows. The
    /// host creates the popup on first use, because the popup needs the canvas that the panel is attached to.
    /// </summary>
    private void CreateComponentActionsMenu(SceneNode host)
        => _componentActionsHost = host.AddComponent<NativePropertyPopupHost>()!;

    /// <summary>Gets the shared actions popup, and creates it when the panel has a canvas.</summary>
    private UIContextMenuComponent? GetComponentActionsMenu()
    {
        if (_componentActionsMenu is { } menu)
            return menu;
        if (_componentActionsHost is not { } host || host.UITransform.GetCanvasTransform() is null)
            return null;
        _componentActionsMenu = host.TryCreatePopup(static () => true, out UIContextMenuComponent? created) ? created : null;
        return _componentActionsMenu;
    }

    /// <summary>Creates the hidden actions button at the right edge of a row's value cell.</summary>
    private (UIButtonComponent Button, UIBoundableTransform Transform) CreateComponentActionsButton(SceneNode valueRoot)
    {
        var created = NativeUIElements.CreateButton(valueRoot, "...", width: ComponentActionsButtonWidth, height: 22.0f,
            margins: new Vector4(0.0f, 2.0f, 4.0f, 2.0f));
        UIBoundableTransform transform = created.Node.GetTransformAs<UIBoundableTransform>(true)!;
        transform.MinAnchor = transform.MinAnchor with { X = 1.0f };
        transform.MaxAnchor = transform.MaxAnchor with { X = 1.0f };
        transform.NormalizedPivot = transform.NormalizedPivot with { X = 1.0f };
        transform.Visibility = EVisibility.Hidden;
        transform.BlocksInputBehind = true;
        created.Button.AccessibilityLabel = "Component actions";
        return (created.Button, transform);
    }

    /// <summary>
    /// Opens the actions menu of the components that one section header edits. A single-node section edits one
    /// component, and a multi-node section edits the matching component of each node.
    /// </summary>
    private void ShowComponentActions(UIButtonComponent button, object?[]? targets)
    {
        if (targets is not { Length: > 0 } || GetComponentActionsMenu() is not { } menu)
            return;
        XRComponent[] components = new XRComponent[targets.Length];
        for (int index = 0; index < targets.Length; index++)
        {
            if (targets[index] is not XRComponent { IsDestroyed: false } component || component.SceneNode is null)
                return;
            components[index] = component;
        }
        // The menu opens below the button and ends at its right edge, so it stays inside the inspector.
        UIBoundableTransform bounds = button.BoundableTransform;
        Vector2 position = bounds.LocalToCanvas(new Vector2(bounds.ActualWidth, 0.0f));
        position.X -= menu.PreferredWidth;
        menu.Show(position,
            new ContextMenuItem("Rename", () => ShowComponentRename(components[0], position), components.Length == 1),
            new ContextMenuItem(components.Length == 1 ? "Remove" : $"Remove from {components.Length} nodes",
                () => RemoveComponents(components)),
            new ContextMenuItem("Inspect alone", () =>
            {
                if (Array.TrueForAll(components, static component => !component.IsDestroyed))
                    InspectedObjects = [.. components];
            }));
    }

    /// <summary>Removes the components through the editor mutation queue, which records the undo step.</summary>
    private void RemoveComponents(XRComponent[] components)
    {
        List<(SceneNode Node, XRComponent Component)> group = new(components.Length);
        foreach (XRComponent component in components)
        {
            if (component.IsDestroyed || component.SceneNode is not { } node)
                return;
            group.Add((node, component));
        }
        // The nodes raise ComponentRemoved, and the inspector rebuilds its sections from that event.
        EditorImGuiUI.QueueNativeComponentRemoveMany(group);
    }

    /// <summary>
    /// Opens a small popup with a text field that renames <paramref name="component"/>. Enter applies the new
    /// name as one undo step, and Escape closes the popup without a change.
    /// </summary>
    private void ShowComponentRename(XRComponent component, Vector2 position)
    {
        if (_componentActionsMenu is not { } menu || component.IsDestroyed)
            return;
        menu.ShowContent(position, SearchFieldHeight + 8.0f, content =>
        {
            SceneNode fieldNode = content.NewChild("ComponentRename");
            UIBoundableTransform bounds = fieldNode.SetTransform<UIBoundableTransform>();
            bounds.MinAnchor = Vector2.Zero;
            bounds.MaxAnchor = Vector2.One;
            bounds.Margins = new Vector4(4.0f);
            var (input, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(fieldNode);
            input.SingleLineMode = true;
            input.Text = string.IsNullOrWhiteSpace(component.Name) ? component.GetType().Name : component.Name;
            input.Submitted += field =>
            {
                string name = field.Text.Trim();
                if (!component.IsDestroyed && name.Length > 0 && name != component.Name)
                {
                    using var _ = Undo.TrackChange("Rename Component", component);
                    component.Name = name;
                    RefreshVirtualProperties();
                }
                menu.Hide();
            };
            input.Cancelled += _ => menu.Hide();
            _componentRenameInput = input;
            _componentRenameFocusAttempts = 0;
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, FocusComponentRenameTick);
        });
    }

    /// <summary>
    /// Gives the rename field the keyboard focus. The field takes focus only after its first published frame, so
    /// the tick tries again on later frames, and stops after a bounded number of tries.
    /// </summary>
    private void FocusComponentRenameTick()
    {
        UITextInputComponent? input = _componentRenameInput;
        bool done = input is null || input.IsDestroyed || ++_componentRenameFocusAttempts > MaxRenameFocusAttempts;
        if (!done && input!.BoundableTransform.GetCanvasComponent()?.GetInputComponent() is { } canvasInput)
            done = ReferenceEquals(canvasInput.FocusedComponent, input) || canvasInput.TryFocusPresented(input);
        if (!done)
            return;
        _componentRenameInput = null;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, FocusComponentRenameTick);
    }

    private void ReleaseComponentActions()
    {
        _componentRenameInput = null;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, FocusComponentRenameTick);
        _componentActionsMenu?.Hide();
        _componentActionsMenu = null;
        _componentActionsHost = null;
    }
}

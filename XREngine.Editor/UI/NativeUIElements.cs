using System.Numerics;
using System.Reflection;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.UI;

public static class NativeUIElements
{
    /// <summary>Create an image that shares its parent's immutable atlas page.</summary>
    public static (SceneNode Node, UIMaterialComponent Image) CreateImage(
        SceneNode parent, UIImageAtlasComponent atlas, string key,
        float width = 24, float height = 24, ColorF4? tint = null)
    {
        var node = parent.NewChild<UIMaterialComponent>(out var image);
        node.Name = key;
        image.BoundableTransform.Width = width;
        image.BoundableTransform.Height = height;
        try
        {
            atlas.Bind(image, key, tint ?? ColorF4.White);
            return (node, image);
        }
        catch
        {
            node.Destroy(true);
            throw;
        }
    }

    public static (SceneNode Node, UIButtonComponent Button, UIMaterialComponent Background, UITextComponent Label) CreateButton(
        SceneNode parent,
        string text,
        Action<UIInteractableComponent>? onClick = null,
        float? width = null,
        float height = 28.0f,
        Vector4? margins = null,
        float? fontSize = null)
    {
        var buttonNode = parent.NewChild<UIButtonComponent, UIMaterialComponent>(out var button, out var background);
        EditorUI.Styles.UpdateButton(button);

        background.SetBatchSolidColor(ColorF4.Transparent);

        var buttonTransform = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
        if (width is not null)
            buttonTransform.Width = width;
        if (height > 0)
            buttonTransform.Height = height;
        buttonTransform.Margins = margins ?? new Vector4(4.0f, 2.0f, 4.0f, 2.0f);

        buttonNode.NewChild<UITextComponent>(out var label);
        label.Text = text;
        label.FontSize = fontSize ?? EditorUI.Styles.PropertyNameFontSize;
        label.Color = EditorUI.Styles.ButtonTextColor;
        label.HorizontalAlignment = EHorizontalAlignment.Center;
        label.VerticalAlignment = EVerticalAlignment.Center;
        label.BoundableTransform.Margins = new Vector4(6.0f, 0.0f, 6.0f, 0.0f);

        SceneNode ringNode = new(buttonNode) { Name = "Button focus ring" };
        UIBoundableTransform ringBounds = ringNode.SetTransform<UIBoundableTransform>();
        ringBounds.MinAnchor = Vector2.Zero;
        ringBounds.MaxAnchor = Vector2.One;
        ringBounds.BlocksInputBehind = false;
        UIMaterialComponent ring = ringNode.AddComponent<UIMaterialComponent>()!;
        ring.SetBatchBorderStyle(ColorF4.Transparent, ColorF4.Transparent, 2);
        button.FocusRingMaterial = ring;

        if (onClick is not null)
            button.RegisterClickActions(onClick);

        return (buttonNode, button, background, label);
    }

    public static (TInput Input, UITextComponent Text, UIPropertyTextDriverComponent Driver, UIMaterialComponent Background) CreateTextInputField<TInput>(
        SceneNode parent,
        PropertyInfo? property = null,
        object?[]? targets = null,
        ColorF4? focusOutlineColor = null,
        ColorF4? unfocusedOutlineColor = null,
        float? fontSize = null,
        Vector4? textMargins = null)
        where TInput : UITextInputComponent
    {
        var matComp = parent.AddComponent<UIMaterialComponent>()!;
        matComp.SetBatchBorderStyle(ColorF4.Transparent, ColorF4.Transparent, 2.0f);

        parent.NewChild<UITextComponent, TInput, UIPropertyTextDriverComponent>(out var textComp, out var textInput, out var textDriver);

        var focusedColor = focusOutlineColor ?? ColorF4.White;
        var unfocusedColor = unfocusedOutlineColor ?? ColorF4.Transparent;

        // The outline shows while the field has keyboard focus or the pointer is over it.
        // Leave and lost-focus events run before their state flag clears, so each
        // handler checks only the other state.
        void SetOutline(bool highlighted)
        {
            Vector4 color = highlighted ? focusedColor : unfocusedColor;
            if (matComp.BatchBorderColor == color)
                return;
            matComp.UITransform.RecordUIChange(UIChangeDomain.Visual);
            matComp.BatchBorderColor = color;
        }
        void Highlight(UIInteractableComponent _) => SetOutline(true);
        void PointerLeft(UIInteractableComponent _) => SetOutline(textInput.CanInteract && textInput.IsFocused);
        void FocusLost(UIInteractableComponent _) => SetOutline(textInput.CanInteract && textInput.IsMouseDirectlyOver);

        textInput.GotFocus += Highlight;
        textInput.MouseDirectOverlapEnter += Highlight;
        textInput.LostFocus += FocusLost;
        textInput.MouseDirectOverlapLeave += PointerLeft;
        if (property is not null && targets is not null)
        {
            textInput.Binding = new EditorTextPropertyBinding(property, targets);
            textInput.IsReadOnly = !property.CanWrite;
        }

        textComp.FontSize = fontSize ?? EditorUI.Styles.PropertyInputFontSize;
        textComp.Color = EditorUI.Styles.PropertyInputTextColor;
        textComp.HorizontalAlignment = EHorizontalAlignment.Left;
        textComp.VerticalAlignment = EVerticalAlignment.Center;
        textComp.BoundableTransform.Margins = textMargins ?? new Vector4(5.0f, 2.0f, 5.0f, 2.0f);
        textComp.ClipToBounds = true;
        textComp.WrapMode = FontGlyphSet.EWrapMode.None;

        textDriver.IsActive = false;

        return (textInput, textComp, textDriver, matComp);
    }

    internal static bool TryCreateTextInputFieldGuarded<TInput>(SceneNode parent,
        PropertyInfo property, object?[] targets, Func<bool> isCurrent,
        out (TInput Input, UITextComponent Text, UIPropertyTextDriverComponent Driver,
            UIMaterialComponent Background) result,
        Action<RuntimeComponentCreationReceipt>? retainFailedReceipt = null)
        where TInput : UITextInputComponent, new()
    {
        result = default;
        if (!isCurrent()) return false;
        ObjectConstructionIdentity parentIdentity = ObjectConstructionIdentity.Capture(parent);
        TransformBase parentTransform = parent.Transform;
        ObjectConstructionIdentity parentTransformIdentity = ObjectConstructionIdentity.Capture(parentTransform);
        TransformBase? grandparentTransform = parentTransform.Parent;
        ObjectConstructionIdentity? grandparentIdentity = grandparentTransform is null
            ? null : ObjectConstructionIdentity.Capture(grandparentTransform);
        EditorVirtualRowControlLease? materialLease = null;
        EditorVirtualRowControlLease? inputLease = null;
        EditorVirtualRowControlLease? textLease = null;
        bool ControlsCurrent() => materialLease is null || inputLease is null || textLease is null
            ? isCurrent()
            : materialLease.IsCurrent(parent) && inputLease.IsCurrent(parent) && textLease.IsCurrent(parent);
        bool ParentCurrent() => ControlsCurrent() && parentIdentity.IsCurrent &&
            parentTransformIdentity.IsCurrent && !parent.IsDestroyed && !parent.IsDestroyQueued &&
            ReferenceEquals(parent.Transform, parentTransform) &&
            (grandparentTransform is null
                ? parentTransform.Parent is null
                : grandparentIdentity is { } identity && identity.IsCurrent &&
                    ReferenceEquals(parentTransform.Parent, grandparentTransform));

        UIMaterialComponent? material = AddGuardedComponent<UIMaterialComponent>(parent, ParentCurrent, retainFailedReceipt);
        if (!ParentCurrent() || material is null) return false;
        material.SetBatchBorderStyle(ColorF4.Transparent, ColorF4.Transparent, 2.0f);
        if (!ParentCurrent()) return false;

        SceneNode? createdTextNode = CreateGuardedBoundableChild(parent, ParentCurrent,
            out UIBoundableTransform? textBounds);
        if (createdTextNode is null || textBounds is null || !ParentCurrent()) return false;
        SceneNode textNode = createdTextNode;
        ObjectConstructionIdentity textNodeIdentity = ObjectConstructionIdentity.Capture(textNode);
        bool TextNodeOwned() => ParentCurrent() && textNodeIdentity.IsCurrent &&
            !textNode.IsDestroyed && !textNode.IsDestroyQueued &&
            ReferenceEquals(textNode.Transform.Parent, parentTransform) &&
            ReferenceEquals(textNode.Transform, textBounds);
        if (!TextNodeOwned()) return false;
        UITextComponent? text = AddGuardedComponent<UITextComponent>(textNode, TextNodeOwned, retainFailedReceipt);
        if (!TextNodeOwned() || text is null) return false;
        TInput? input = AddGuardedComponent<TInput>(textNode, TextNodeOwned, retainFailedReceipt);
        if (!TextNodeOwned() || input is null) return false;
        UIPropertyTextDriverComponent? driver = AddGuardedComponent<UIPropertyTextDriverComponent>(textNode,
            TextNodeOwned, retainFailedReceipt);
        if (!TextNodeOwned() || driver is null) return false;
        ObjectConstructionIdentity textIdentity = ObjectConstructionIdentity.Capture(text);
        ObjectConstructionIdentity inputIdentity = ObjectConstructionIdentity.Capture(input);
        ObjectConstructionIdentity driverIdentity = ObjectConstructionIdentity.Capture(driver);
        ObjectConstructionIdentity textBoundsIdentity = ObjectConstructionIdentity.Capture(textBounds);
        bool Current() => TextNodeOwned() && textIdentity.IsCurrent && inputIdentity.IsCurrent &&
            driverIdentity.IsCurrent && textBoundsIdentity.IsCurrent &&
            ReferenceEquals(text.SceneNode, textNode) && ReferenceEquals(input.SceneNode, textNode) &&
            ReferenceEquals(driver.SceneNode, textNode) && ReferenceEquals(textNode.Transform, textBounds) &&
            !text.IsDestroyed && !text.IsDestroyQueued && !input.IsDestroyed && !input.IsDestroyQueued &&
            !driver.IsDestroyed && !driver.IsDestroyQueued;
        if (!Current()) return false;

        input.Binding = new EditorTextPropertyBinding(property, targets);
        if (!Current()) return false;
        input.IsReadOnly = !property.CanWrite;
        if (!Current()) return false;
        void SetOutline(bool highlighted)
        {
            if (!Current()) return;
            ColorF4 color = highlighted ? ColorF4.White : ColorF4.Transparent;
            if (material.BatchBorderColor == color) return;
            material.UITransform.RecordUIChange(UIChangeDomain.Visual);
            if (!Current()) return;
            material.BatchBorderColor = color;
        }
        void Highlight(UIInteractableComponent _) => SetOutline(true);
        void PointerLeft(UIInteractableComponent _) => SetOutline(input.CanInteract && input.IsFocused);
        void FocusLost(UIInteractableComponent _) => SetOutline(input.CanInteract && input.IsMouseDirectlyOver);
        input.GotFocus += Highlight;
        input.MouseDirectOverlapEnter += Highlight;
        input.LostFocus += FocusLost;
        input.MouseDirectOverlapLeave += PointerLeft;

        text.FontSize = EditorUI.Styles.PropertyInputFontSize;
        if (!Current()) return false;
        text.Color = EditorUI.Styles.PropertyInputTextColor;
        if (!Current()) return false;
        text.HorizontalAlignment = EHorizontalAlignment.Left;
        if (!Current()) return false;
        text.VerticalAlignment = EVerticalAlignment.Center;
        if (!Current()) return false;
        textBounds.Margins = new Vector4(5.0f, 2.0f, 5.0f, 2.0f);
        if (!Current()) return false;
        text.ClipToBounds = true;
        if (!Current()) return false;
        text.WrapMode = FontGlyphSet.EWrapMode.None;
        if (!Current()) return false;
        driver.IsActive = false;
        if (!Current()) return false;
        materialLease = EditorVirtualRowControlLease.Capture(material, parent);
        inputLease = EditorVirtualRowControlLease.Capture(input, parent);
        textLease = EditorVirtualRowControlLease.Capture(text, parent);
        if (!Current()) return false;
        result = (input, text, driver, material);
        return true;
    }

    private static TComponent? AddGuardedComponent<TComponent>(SceneNode node,
        Func<bool> isCurrent, Action<RuntimeComponentCreationReceipt>? retainFailedReceipt = null)
        where TComponent : XRComponent, new()
    {
        if (!isCurrent()) return null;
        RuntimeComponentCreationReceipt receipt = new(node, isCurrent);
        try
        {
            TComponent? component = node.AddComponentGuarded(static () => new TComponent(), receipt);
            if (component is not null && isCurrent() &&
                ReferenceEquals(component.SceneNode, node) &&
                !component.IsDestroyed && !component.IsDestroyQueued)
                return component;
            InvalidOperationException ownershipFailure = new("The guarded editor component owner changed after construction.");
            receipt.RecordFailure(ownershipFailure);
            if (receipt.Component is { IsDestroyed: false, IsDestroyQueued: false } &&
                !receipt.TryAbortUnchangedComponent())
            {
                retainFailedReceipt?.Invoke(receipt);
                Debug.LogException(receipt.CleanupFailure is { } cleanup
                    ? new AggregateException(ownershipFailure, cleanup) : ownershipFailure,
                    "Could not abort an editor component after its owner changed.");
            }
            return null;
        }
        catch (Exception failure)
        {
            receipt.RecordFailure(failure);
            bool aborted = receipt.Component is null or { IsDestroyed: true } or { IsDestroyQueued: true } ||
                receipt.TryAbortUnchangedComponent();
            if (!aborted)
            {
                retainFailedReceipt?.Invoke(receipt);
                Debug.LogException(receipt.CleanupFailure is { } cleanup
                    ? new AggregateException(failure, cleanup) : failure,
                    "Could not abort a failed editor component creation.");
            }
            if (failure is InvalidOperationException && !isCurrent()) return null;
            throw;
        }
    }

    private static SceneNode? CreateGuardedBoundableChild(SceneNode parent, Func<bool> isCurrent,
        out UIBoundableTransform? bounds)
    {
        bounds = null;
        if (!isCurrent()) return null;
        ObjectConstructionIdentity parentIdentity = ObjectConstructionIdentity.Capture(parent);
        TransformBase parentTransform = parent.Transform;
        ObjectConstructionIdentity parentTransformIdentity = ObjectConstructionIdentity.Capture(parentTransform);
        SceneNode child = parent.NewChild();
        ObjectConstructionIdentity childIdentity = ObjectConstructionIdentity.Capture(child);
        bool ChildCurrent() => isCurrent() && parentIdentity.IsCurrent && parentTransformIdentity.IsCurrent &&
            childIdentity.IsCurrent && !parent.IsDestroyed && !parent.IsDestroyQueued &&
            !child.IsDestroyed && !child.IsDestroyQueued && ReferenceEquals(parent.Transform, parentTransform) &&
            ReferenceEquals(child.Transform.Parent, parentTransform);
        if (!ChildCurrent()) return null;
        UIBoundableTransform childBounds = child.SetTransform<UIBoundableTransform>();
        ObjectConstructionIdentity boundsIdentity = ObjectConstructionIdentity.Capture(childBounds);
        if (!ChildCurrent() || !boundsIdentity.IsCurrent || childBounds.IsDestroyed || childBounds.IsDestroyQueued ||
            !ReferenceEquals(child.Transform, childBounds)) return null;
        bounds = childBounds;
        return child;
    }

    public static (UIToggleComponent Toggle, UIMaterialComponent Background, UITextComponent Glyph) CreateCheckboxToggle(
        SceneNode parent,
        PropertyInfo? property = null,
        object?[]? targets = null,
        float size = 20.0f,
        float glyphFontSize = 16.0f)
    {
        var materialComponent = parent.AddComponent<UIMaterialComponent>()!;
        materialComponent.SetBatchBorderStyle(ColorF4.Black, ColorF4.Gray, 2.0f);

        var tfm = materialComponent.BoundableTransform;
        tfm.MinAnchor = new Vector2(0.0f, 0.0f);
        tfm.MaxAnchor = new Vector2(0.0f, 0.0f);
        tfm.Width = size;
        tfm.Height = size;

        parent.NewChild(out UIToggleComponent toggle);
        if (property is not null && targets is not null)
        {
            toggle.Binding = new EditorTogglePropertyBinding(property, targets);
            toggle.IsReadOnly = !property.CanWrite;
        }

        parent.NewChild(out UITextComponent glyph, "ToggleLabel");
        glyph.Text = string.Empty;
        glyph.Font = FontGlyphSet.LoadDefaultUIIconFont();
        glyph.FontSize = glyphFontSize;
        glyph.Color = ColorF4.Gray;
        glyph.HorizontalAlignment = EHorizontalAlignment.Center;
        glyph.VerticalAlignment = EVerticalAlignment.Center;

        void SetCheckState(UIToggleComponent.ECurrentState state)
        {
            glyph.Text = state switch
            {
                UIToggleComponent.ECurrentState.True => "\u2713",
                UIToggleComponent.ECurrentState.Intermediate => "\u2212",
                _ => string.Empty,
            };
        }

        void GotFocus(UIInteractableComponent _)
        {
            materialComponent.UITransform.RecordUIChange(UIChangeDomain.Visual);
            materialComponent.BatchBorderColor = ColorF4.White;
            glyph.Color = ColorF4.White;
        }

        void LostFocus(UIInteractableComponent _)
        {
            if (toggle.CanInteract && toggle.IsMouseDirectlyOver)
                return;
            materialComponent.UITransform.RecordUIChange(UIChangeDomain.Visual);
            materialComponent.BatchBorderColor = ColorF4.Gray;
            glyph.Color = ColorF4.Gray;
        }

        void MouseLeave(UIInteractableComponent _)
        {
            if (toggle.CanInteract && toggle.IsFocused)
                return;
            materialComponent.UITransform.RecordUIChange(UIChangeDomain.Visual);
            materialComponent.BatchBorderColor = ColorF4.Gray;
            glyph.Color = ColorF4.Gray;
        }

        toggle.OnStateChanged += SetCheckState;
        toggle.GotFocus += GotFocus;
        toggle.LostFocus += LostFocus;
        toggle.MouseDirectOverlapEnter += GotFocus;
        toggle.MouseDirectOverlapLeave += MouseLeave;
        SetCheckState(toggle.CurrentState);

        return (toggle, materialComponent, glyph);
    }

    internal static bool TryCreateCheckboxToggleGuarded(SceneNode parent, PropertyInfo property,
        object?[] targets, Func<bool> isCurrent,
        out (UIToggleComponent Toggle, UIMaterialComponent Background, UITextComponent Glyph) result,
        float size = 20.0f, float glyphFontSize = 16.0f,
        Action<RuntimeComponentCreationReceipt>? retainFailedReceipt = null)
    {
        result = default;
        if (!isCurrent()) return false;
        ObjectConstructionIdentity parentIdentity = ObjectConstructionIdentity.Capture(parent);
        TransformBase parentTransform = parent.Transform;
        ObjectConstructionIdentity parentTransformIdentity = ObjectConstructionIdentity.Capture(parentTransform);
        EditorVirtualRowControlLease? materialLease = null;
        EditorVirtualRowControlLease? toggleLease = null;
        EditorVirtualRowControlLease? glyphLease = null;
        bool Current() => materialLease is null || toggleLease is null || glyphLease is null
            ? isCurrent() && parentIdentity.IsCurrent && parentTransformIdentity.IsCurrent &&
                !parent.IsDestroyed && !parent.IsDestroyQueued && ReferenceEquals(parent.Transform, parentTransform)
            : materialLease.IsCurrent(parent) && toggleLease.IsCurrent(parent) && glyphLease.IsCurrent(parent);
        UIMaterialComponent? material = AddGuardedComponent<UIMaterialComponent>(parent, Current, retainFailedReceipt);
        if (!Current() || material is null) return false;
        material.SetBatchBorderStyle(ColorF4.Black, ColorF4.Gray, 2.0f);
        if (!Current()) return false;
        UIBoundableTransform bounds = material.BoundableTransform;
        if (!Current() || !ReferenceEquals(parent.Transform, bounds)) return false;
        bounds.MinAnchor = new Vector2(0.0f, 0.0f);
        if (!Current()) return false;
        bounds.MaxAnchor = new Vector2(0.0f, 0.0f);
        if (!Current()) return false;
        bounds.Width = size;
        if (!Current()) return false;
        bounds.Height = size;
        if (!Current()) return false;

        SceneNode? createdToggleNode = CreateGuardedBoundableChild(parent, Current, out UIBoundableTransform? toggleBounds);
        if (createdToggleNode is null || toggleBounds is null || !Current()) return false;
        SceneNode toggleNode = createdToggleNode;
        ObjectConstructionIdentity toggleNodeIdentity = ObjectConstructionIdentity.Capture(toggleNode);
        ObjectConstructionIdentity toggleBoundsIdentity = ObjectConstructionIdentity.Capture(toggleBounds);
        bool ToggleNodeCurrent() => Current() && toggleNodeIdentity.IsCurrent && toggleBoundsIdentity.IsCurrent &&
            !toggleNode.IsDestroyed && !toggleNode.IsDestroyQueued &&
            ReferenceEquals(toggleNode.Transform.Parent, parentTransform) && ReferenceEquals(toggleNode.Transform, toggleBounds);
        UIToggleComponent? toggle = AddGuardedComponent<UIToggleComponent>(toggleNode, ToggleNodeCurrent, retainFailedReceipt);
        if (!ToggleNodeCurrent() || toggle is null) return false;
        toggle.Binding = new EditorTogglePropertyBinding(property, targets);
        if (!ToggleNodeCurrent()) return false;
        toggle.IsReadOnly = !property.CanWrite;
        if (!ToggleNodeCurrent()) return false;

        SceneNode? createdGlyphNode = CreateGuardedBoundableChild(parent, Current, out UIBoundableTransform? glyphBounds);
        if (createdGlyphNode is null || glyphBounds is null || !Current()) return false;
        SceneNode glyphNode = createdGlyphNode;
        ObjectConstructionIdentity glyphNodeIdentity = ObjectConstructionIdentity.Capture(glyphNode);
        ObjectConstructionIdentity glyphBoundsIdentity = ObjectConstructionIdentity.Capture(glyphBounds);
        bool GlyphNodeCurrent() => Current() && glyphNodeIdentity.IsCurrent && glyphBoundsIdentity.IsCurrent &&
            !glyphNode.IsDestroyed && !glyphNode.IsDestroyQueued &&
            ReferenceEquals(glyphNode.Transform.Parent, parentTransform) && ReferenceEquals(glyphNode.Transform, glyphBounds);
        UITextComponent? glyph = AddGuardedComponent<UITextComponent>(glyphNode, GlyphNodeCurrent, retainFailedReceipt);
        if (!GlyphNodeCurrent() || glyph is null) return false;
        try
        {
            glyph.SetTextGuarded(null, GlyphNodeCurrent, static current =>
            {
                if (!current()) throw new InvalidOperationException("The nested checkbox owner changed during text setup.");
            });
        }
        catch (InvalidOperationException) when (!GlyphNodeCurrent()) { return false; }
        if (!GlyphNodeCurrent()) return false;
        glyph.Font = FontGlyphSet.LoadDefaultUIIconFont();
        if (!GlyphNodeCurrent()) return false;
        glyph.FontSize = glyphFontSize;
        if (!GlyphNodeCurrent()) return false;
        glyph.Color = ColorF4.Gray;
        if (!GlyphNodeCurrent()) return false;
        glyph.HorizontalAlignment = EHorizontalAlignment.Center;
        if (!GlyphNodeCurrent()) return false;
        glyph.VerticalAlignment = EVerticalAlignment.Center;
        if (!GlyphNodeCurrent()) return false;

        void SetCheckState(UIToggleComponent.ECurrentState state)
        {
            if (!Current()) return;
            string? text = state switch
            {
                UIToggleComponent.ECurrentState.True => "\u2713",
                UIToggleComponent.ECurrentState.Intermediate => "\u2212",
                _ => null,
            };
            try
            {
                glyph.SetTextGuarded(text, Current, static current =>
                {
                    if (!current()) throw new InvalidOperationException("The nested checkbox owner changed during state update.");
                });
            }
            catch (InvalidOperationException) when (!Current()) { }
        }

        void SetBorder(ColorF4 color)
        {
            if (!Current()) return;
            material.UITransform.RecordUIChange(UIChangeDomain.Visual);
            if (!Current()) return;
            material.BatchBorderColor = color;
        }
        void GotFocus(UIInteractableComponent _)
        {
            if (!Current()) return;
            SetBorder(ColorF4.White);
            if (Current()) glyph.Color = ColorF4.White;
        }
        void LostFocus(UIInteractableComponent _)
        {
            if (!Current() || toggle.CanInteract && toggle.IsMouseDirectlyOver) return;
            SetBorder(ColorF4.Gray);
            if (Current()) glyph.Color = ColorF4.Gray;
        }
        void MouseLeave(UIInteractableComponent _)
        {
            if (!Current() || toggle.CanInteract && toggle.IsFocused) return;
            SetBorder(ColorF4.Gray);
            if (Current()) glyph.Color = ColorF4.Gray;
        }
        toggle.OnStateChanged += SetCheckState;
        toggle.GotFocus += GotFocus;
        toggle.LostFocus += LostFocus;
        toggle.MouseDirectOverlapEnter += GotFocus;
        toggle.MouseDirectOverlapLeave += MouseLeave;
        SetCheckState(toggle.CurrentState);
        if (!Current()) return false;
        materialLease = EditorVirtualRowControlLease.Capture(material, parent);
        toggleLease = EditorVirtualRowControlLease.Capture(toggle, parent);
        glyphLease = EditorVirtualRowControlLease.Capture(glyph, parent);
        if (!Current()) return false;
        result = (toggle, material, glyph);
        return true;
    }

    /// <summary>Rebinds pooled primitive editors without retaining prior property targets.</summary>
    public static void RebindPropertyEditor(SceneNode root, PropertyInfo? property, object?[]? targets,
        Func<bool>? ownerIsCurrent = null)
    {
        if (ownerIsCurrent?.Invoke() == false) return;
        for (int index = 0; index < root.Components.Count; index++)
        {
            switch (root.Components[index])
            {
                case InspectorEnumEditorComponent enumEditor:
                    enumEditor.Bind(property, targets, ownerIsCurrent);
                    break;
                case UIToggleComponent toggle:
                    toggle.Binding = property is not null && targets is not null
                        ? new EditorTogglePropertyBinding(property, targets) : null;
                    if (ownerIsCurrent?.Invoke() == false) return;
                    toggle.Property = null;
                    if (ownerIsCurrent?.Invoke() == false) return;
                    toggle.Targets = null;
                    if (ownerIsCurrent?.Invoke() == false) return;
                    toggle.IsReadOnly = property is null || !property.CanWrite;
                    break;
                case UITextInputComponent text:
                    text.Binding = property is not null && targets is not null
                        ? new EditorTextPropertyBinding(property, targets) : null;
                    if (ownerIsCurrent?.Invoke() == false) return;
                    text.Property = null;
                    if (ownerIsCurrent?.Invoke() == false) return;
                    text.Targets = null;
                    if (ownerIsCurrent?.Invoke() == false) return;
                    text.IsReadOnly = property is null || !property.CanWrite;
                    break;
                case UIPropertyTextDriverComponent driver:
                    driver.Property = null;
                    if (ownerIsCurrent?.Invoke() == false) return;
                    driver.Sources = null;
                    if (ownerIsCurrent?.Invoke() == false) return;
                    driver.IsActive = false;
                    break;
            }
            if (ownerIsCurrent?.Invoke() == false) return;
        }
        for (int index = 0; index < root.Transform.Children.Count; index++)
        {
            if (ownerIsCurrent?.Invoke() == false) return;
            if (root.Transform.Children[index].SceneNode is { } child)
                RebindPropertyEditor(child, property, targets, ownerIsCurrent);
        }
    }

}

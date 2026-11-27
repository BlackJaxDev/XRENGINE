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
    /// <summary>One shared button style, so buttons do not each build their own copy.</summary>
    private static readonly UIStyle SharedButtonStyle = EditorGlassTheme.Button;

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
        // The glass style animates the fill, corners, and text color for hover, press, and focus.
        button.Style = SharedButtonStyle;

        var buttonTransform = buttonNode.GetTransformAs<UIBoundableTransform>(true)!;
        // With stretched anchors, Width and Height add to the parent's extent. A button with a set size
        // uses point anchors on that axis instead, so its size is exactly the requested size. It sits at
        // the left of its slot and at the vertical middle.
        if (width is not null)
        {
            buttonTransform.MinAnchor = new Vector2(0.0f, buttonTransform.MinAnchor.Y);
            buttonTransform.MaxAnchor = new Vector2(0.0f, buttonTransform.MaxAnchor.Y);
            buttonTransform.NormalizedPivot = new Vector2(0.0f, buttonTransform.NormalizedPivot.Y);
            buttonTransform.Width = width;
        }
        if (height > 0)
        {
            buttonTransform.MinAnchor = new Vector2(buttonTransform.MinAnchor.X, 0.5f);
            buttonTransform.MaxAnchor = new Vector2(buttonTransform.MaxAnchor.X, 0.5f);
            buttonTransform.NormalizedPivot = new Vector2(buttonTransform.NormalizedPivot.X, 0.5f);
            buttonTransform.Height = height;
        }
        buttonTransform.Margins = margins ?? new Vector4(4.0f, 2.0f, 4.0f, 2.0f);

        buttonNode.NewChild<UITextComponent>(out var label);
        label.Text = text;
        label.FontSize = fontSize ?? EditorUI.Styles.PropertyNameFontSize;
        label.Color = EditorGlassTheme.TextPrimary;
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

    private static readonly UIQuadStyle FieldQuadStyle = EditorGlassTheme.Field.ToQuadStyle();
    private static readonly UIQuadStyle FocusedFieldQuadStyle = FocusedField().ToQuadStyle();

    private static UIStyleProperties FocusedField()
    {
        UIStyleProperties style = EditorGlassTheme.Field;
        style.BorderColor = EditorGlassTheme.Accent;
        style.BorderWidth = 1.25f;
        return style;
    }

    /// <summary>
    /// Creates a vector icon node centered at <paramref name="anchor"/> of <paramref name="parent"/>, moved by
    /// <paramref name="offset"/>. The UI quad shader draws the icon, so it needs no font glyph.
    /// </summary>
    internal static UIMaterialComponent CreateIcon(SceneNode parent, EUIQuadIcon icon, Vector2 anchor, Vector2 offset,
        float size, ColorF4 color, float strokeWidth = 1.6f)
    {
        SceneNode node = parent.NewChild<UIMaterialComponent>(out var quad);
        node.Name = "Icon";
        UIBoundableTransform transform = node.GetTransformAs<UIBoundableTransform>(true)!;
        transform.MinAnchor = anchor;
        transform.MaxAnchor = anchor;
        transform.NormalizedPivot = new Vector2(0.5f, 0.5f);
        transform.Translation = offset;
        transform.Width = size;
        transform.Height = size;
        transform.BlocksInputBehind = false;
        quad.SetBatchSolidColor(new Vector4(color.R, color.G, color.B, color.A));
        quad.QuadStyle = new UIStyleProperties { Icon = icon, BorderWidth = strokeWidth, DecodeSrgbColors = true }.ToQuadStyle();
        return quad;
    }

    /// <summary>
    /// Gives a text field background the glass field look: a soft rounded well with a hairline border.
    /// </summary>
    internal static void ApplyFieldStyle(UIMaterialComponent background)
    {
        ColorF4 fill = EditorGlassTheme.FieldFill;
        background.SetBatchSolidColor(new Vector4(fill.R, fill.G, fill.B, fill.A));
        background.QuadStyle = FieldQuadStyle;
    }

    /// <summary>
    /// Shows the accent border of a field while it has focus or the pointer is over it. It writes the style only
    /// when the state changes, because each style write makes the canvas publish again.
    /// </summary>
    internal static void SetFieldHighlight(UIMaterialComponent background, bool highlighted)
    {
        UIQuadStyle style = highlighted ? FocusedFieldQuadStyle : FieldQuadStyle;
        if (background.QuadStyle != style)
            background.QuadStyle = style;
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
        // A caller that gives its own outline colors keeps the plain border look. Other fields use the glass
        // field style.
        bool customOutline = focusOutlineColor is not null || unfocusedOutlineColor is not null;
        if (customOutline)
            matComp.SetBatchBorderStyle(ColorF4.Transparent, ColorF4.Transparent, 2.0f);
        else
            ApplyFieldStyle(matComp);

        parent.NewChild<UITextComponent, TInput, UIPropertyTextDriverComponent>(out var textComp, out var textInput, out var textDriver);

        var focusedColor = focusOutlineColor ?? ColorF4.White;
        var unfocusedColor = unfocusedOutlineColor ?? ColorF4.Transparent;

        // The outline shows while the field has keyboard focus or the pointer is over it.
        // Leave and lost-focus events run before their state flag clears, so each
        // handler checks only the other state.
        void SetOutline(bool highlighted)
        {
            if (!customOutline)
            {
                SetFieldHighlight(matComp, highlighted);
                return;
            }
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
        ApplyFieldStyle(material);
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
            SetFieldHighlight(material, highlighted);
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

    // A checked box fills with the accent color and shows a white Lucide check mark. A mixed state shows a
    // dash on a half-strength fill. The glyph stays white; hover and focus change only the border.
    private static readonly ColorF4 CheckboxOffFill = new(0.0f, 0.0f, 0.0f, 0.22f);

    private static ColorF4 CheckboxFill(UIToggleComponent.ECurrentState state)
    {
        ColorF4 accent = EditorGlassTheme.Accent;
        return state switch
        {
            UIToggleComponent.ECurrentState.True => accent,
            UIToggleComponent.ECurrentState.Intermediate => new ColorF4(accent.R, accent.G, accent.B, 0.45f),
            _ => CheckboxOffFill,
        };
    }

    /// <summary>
    /// Draws a checkbox box like the hierarchy toggle: a rounded quad in the glass style. A checked or mixed box
    /// has the accent fill and no outline; an empty box has a soft outline that brightens under the pointer.
    /// </summary>
    private static void ApplyCheckboxBox(UIMaterialComponent box, UIToggleComponent.ECurrentState state, bool highlighted)
    {
        ColorF4 fill = CheckboxFill(state);
        box.SetBatchSolidColor(new Vector4(fill.R, fill.G, fill.B, fill.A));
        bool filled = state != UIToggleComponent.ECurrentState.False;
        UIQuadStyle style = filled ? CheckboxFilledStyle : highlighted ? CheckboxEmptyHoverStyle : CheckboxEmptyStyle;
        if (box.QuadStyle != style)
            box.QuadStyle = style;
    }

    private static readonly UIQuadStyle CheckboxFilledStyle = new UIStyleProperties
    {
        CornerRadii = new Vector4(4.0f),
        DecodeSrgbColors = true,
    }.ToQuadStyle();

    private static readonly UIQuadStyle CheckboxEmptyStyle = new UIStyleProperties
    {
        CornerRadii = new Vector4(4.0f),
        BorderWidth = 1.25f,
        BorderColor = EditorGlassTheme.TextDisabled,
        DecodeSrgbColors = true,
    }.ToQuadStyle();

    private static readonly UIQuadStyle CheckboxEmptyHoverStyle = new UIStyleProperties
    {
        CornerRadii = new Vector4(4.0f),
        BorderWidth = 1.25f,
        BorderColor = EditorGlassTheme.TextSecondary,
        DecodeSrgbColors = true,
    }.ToQuadStyle();

    private static readonly UIQuadStyle CheckboxCheckIcon = new UIStyleProperties
    {
        Icon = EUIQuadIcon.Check, BorderWidth = 1.7f, DecodeSrgbColors = true,
    }.ToQuadStyle();

    private static readonly UIQuadStyle CheckboxMixedIcon = new UIStyleProperties
    {
        Icon = EUIQuadIcon.Minus, BorderWidth = 1.7f, DecodeSrgbColors = true,
    }.ToQuadStyle();

    /// <summary>Shows the shader check icon for a checked box, a dash for a mixed box, and nothing for an empty box.</summary>
    private static void ApplyCheckboxMark(UIMaterialComponent mark, UIToggleComponent.ECurrentState state)
    {
        EVisibility visibility = state == UIToggleComponent.ECurrentState.False ? EVisibility.Hidden : EVisibility.Visible;
        if (mark.BoundableTransform.Visibility != visibility)
            mark.BoundableTransform.Visibility = visibility;
        UIQuadStyle style = state == UIToggleComponent.ECurrentState.Intermediate ? CheckboxMixedIcon : CheckboxCheckIcon;
        if (mark.QuadStyle != style)
            mark.QuadStyle = style;
    }

    private static string CheckboxGlyph(UIToggleComponent.ECurrentState state)
        => state switch
        {
            UIToggleComponent.ECurrentState.True => EditorIcons.Check,
            UIToggleComponent.ECurrentState.Intermediate => EditorIcons.Minus,
            _ => string.Empty,
        };

    public static (UIToggleComponent Toggle, UIMaterialComponent Background, UITextComponent Glyph) CreateCheckboxToggle(
        SceneNode parent,
        PropertyInfo? property = null,
        object?[]? targets = null,
        float size = 16.0f,
        float glyphFontSize = 16.0f)
    {
        var materialComponent = parent.AddComponent<UIMaterialComponent>()!;
        ApplyCheckboxBox(materialComponent, UIToggleComponent.ECurrentState.False, highlighted: false);

        // The box sits at the left of its cell, centered on the row.
        var tfm = materialComponent.BoundableTransform;
        tfm.MinAnchor = new Vector2(0.0f, 0.5f);
        tfm.MaxAnchor = new Vector2(0.0f, 0.5f);
        tfm.NormalizedPivot = new Vector2(0.0f, 0.5f);
        tfm.Width = size;
        tfm.Height = size;

        parent.NewChild(out UIToggleComponent toggle);
        if (property is not null && targets is not null)
        {
            toggle.Binding = new EditorTogglePropertyBinding(property, targets);
            toggle.IsReadOnly = !property.CanWrite;
        }

        // The check mark is a shader vector icon like the hierarchy toggle's, so it is sharp at this size.
        UIMaterialComponent mark = CreateIcon(parent, EUIQuadIcon.Check, new Vector2(0.5f, 0.5f), Vector2.Zero,
            MathF.Round(size * 0.62f), ColorF4.White, strokeWidth: 1.7f);
        mark.BoundableTransform.Visibility = EVisibility.Hidden;

        // The text glyph stays for callers that read it, but it shows nothing.
        parent.NewChild(out UITextComponent glyph, "ToggleLabel");
        glyph.Text = string.Empty;
        glyph.HorizontalAlignment = EHorizontalAlignment.Center;
        glyph.VerticalAlignment = EVerticalAlignment.Center;

        bool highlighted = false;
        void SetCheckState(UIToggleComponent.ECurrentState state)
        {
            ApplyCheckboxBox(materialComponent, state, highlighted);
            ApplyCheckboxMark(mark, state);
        }

        void GotFocus(UIInteractableComponent _)
        {
            highlighted = true;
            ApplyCheckboxBox(materialComponent, toggle.CurrentState, highlighted);
        }

        void LostFocus(UIInteractableComponent _)
        {
            if (toggle.CanInteract && toggle.IsMouseDirectlyOver)
                return;
            highlighted = false;
            ApplyCheckboxBox(materialComponent, toggle.CurrentState, highlighted);
        }

        void MouseLeave(UIInteractableComponent _)
        {
            if (toggle.CanInteract && toggle.IsFocused)
                return;
            highlighted = false;
            ApplyCheckboxBox(materialComponent, toggle.CurrentState, highlighted);
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
        float size = 16.0f, float glyphFontSize = 12.0f,
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
        ApplyCheckboxBox(material, UIToggleComponent.ECurrentState.False, highlighted: false);
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
        glyph.Font = EditorIcons.Font;
        if (!GlyphNodeCurrent()) return false;
        glyph.FontSize = glyphFontSize;
        if (!GlyphNodeCurrent()) return false;
        glyph.Color = ColorF4.White;
        if (!GlyphNodeCurrent()) return false;
        glyph.HorizontalAlignment = EHorizontalAlignment.Center;
        if (!GlyphNodeCurrent()) return false;
        glyph.VerticalAlignment = EVerticalAlignment.Center;
        if (!GlyphNodeCurrent()) return false;

        bool highlighted = false;
        void SetCheckState(UIToggleComponent.ECurrentState state)
        {
            if (!Current()) return;
            ApplyCheckboxBox(material, state, highlighted);
            if (!Current()) return;
            string? text = CheckboxGlyph(state);
            try
            {
                glyph.SetTextGuarded(text, Current, static current =>
                {
                    if (!current()) throw new InvalidOperationException("The nested checkbox owner changed during state update.");
                });
            }
            catch (InvalidOperationException) when (!Current()) { }
        }

        void SetBorder(bool highlight)
        {
            if (!Current()) return;
            highlighted = highlight;
            ApplyCheckboxBox(material, toggle.CurrentState, highlighted);
        }
        void GotFocus(UIInteractableComponent _)
        {
            if (!Current()) return;
            SetBorder(true);
            if (Current()) glyph.Color = ColorF4.White;
        }
        void LostFocus(UIInteractableComponent _)
        {
            if (!Current() || toggle.CanInteract && toggle.IsMouseDirectlyOver) return;
            SetBorder(false);
            if (Current()) glyph.Color = ColorF4.White;
        }
        void MouseLeave(UIInteractableComponent _)
        {
            if (!Current() || toggle.CanInteract && toggle.IsFocused) return;
            SetBorder(false);
            if (Current()) glyph.Color = ColorF4.White;
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
        // A vector or rotation editor binds its fields to a transformer component, and the transformer binds
        // to the source property. Only the transformer changes target, so its fields are not rebound.
        if (root.GetComponent<InspectorPropertyEditors.DataTransformerBase>() is { } transformer)
        {
            transformer.Targets = null;
            if (ownerIsCurrent?.Invoke() == false) return;
            transformer.Property = property;
            if (ownerIsCurrent?.Invoke() == false) return;
            transformer.Targets = targets;
            return;
        }
        for (int index = 0; index < root.Components.Count; index++)
        {
            switch (root.Components[index])
            {
                case UIColorSelectorComponent color:
                    color.Binding = property is not null && targets is not null
                        ? new EditorColorPropertyBinding(property, targets) : null;
                    if (ownerIsCurrent?.Invoke() == false) return;
                    color.IsReadOnly = property is null || !property.CanWrite;
                    break;
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

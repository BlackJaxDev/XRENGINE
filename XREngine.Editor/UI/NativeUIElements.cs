using System.Numerics;
using System.Reflection;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Scene;

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

        void GotFocus(UIInteractableComponent _)
        {
            matComp.UITransform.RecordUIChange(UIChangeDomain.Visual);
            matComp.BatchBorderColor = focusedColor;
        }
        void LostFocus(UIInteractableComponent _)
        {
            matComp.UITransform.RecordUIChange(UIChangeDomain.Visual);
            matComp.BatchBorderColor = unfocusedColor;
        }

        textInput.MouseDirectOverlapEnter += GotFocus;
        textInput.MouseDirectOverlapLeave += LostFocus;
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
            materialComponent.UITransform.RecordUIChange(UIChangeDomain.Visual);
            materialComponent.BatchBorderColor = ColorF4.Gray;
            glyph.Color = ColorF4.Gray;
        }

        toggle.OnStateChanged += SetCheckState;
        toggle.MouseDirectOverlapEnter += GotFocus;
        toggle.MouseDirectOverlapLeave += LostFocus;
        SetCheckState(toggle.CurrentState);

        return (toggle, materialComponent, glyph);
    }

    /// <summary>Rebinds pooled primitive editors without retaining prior property targets.</summary>
    public static void RebindPropertyEditor(SceneNode root, PropertyInfo? property, object?[]? targets)
    {
        for (int index = 0; index < root.Components.Count; index++)
        {
            switch (root.Components[index])
            {
                case UIToggleComponent toggle:
                    toggle.Binding = property is not null && targets is not null
                        ? new EditorTogglePropertyBinding(property, targets) : null;
                    toggle.Property = null;
                    toggle.Targets = null;
                    toggle.IsReadOnly = property is null || !property.CanWrite;
                    break;
                case UITextInputComponent text:
                    text.Binding = property is not null && targets is not null
                        ? new EditorTextPropertyBinding(property, targets) : null;
                    text.Property = null;
                    text.Targets = null;
                    text.IsReadOnly = property is null || !property.CanWrite;
                    break;
                case UIPropertyTextDriverComponent driver:
                    driver.Property = null;
                    driver.Sources = null;
                    driver.IsActive = false;
                    break;
            }
        }
        for (int index = 0; index < root.Transform.Children.Count; index++)
            if (root.Transform.Children[index].SceneNode is { } child)
                RebindPropertyEditor(child, property, targets);
    }

}

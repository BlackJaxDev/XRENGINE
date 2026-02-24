using System.Numerics;
using System.Reflection;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI;

public static class NativeUIElements
{
    private const string OutlineColorUniformName = "OutlineColor";
    private const string OutlineWidthUniformName = "OutlineWidth";
    private const string FillColorUniformName = "FillColor";

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

        var mat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Transparent);
        mat.EnableTransparency();
        background.Material = mat;

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
        var mat = CreateOutlineMaterial()!;
        matComp.Material = mat;

        parent.NewChild<UITextComponent, TInput, UIPropertyTextDriverComponent>(out var textComp, out var textInput, out var textDriver);

        var focusedColor = focusOutlineColor ?? ColorF4.White;
        var unfocusedColor = unfocusedOutlineColor ?? ColorF4.Transparent;

        void GotFocus(UIInteractableComponent _) => mat.SetVector4(OutlineColorUniformName, focusedColor);
        void LostFocus(UIInteractableComponent _) => mat.SetVector4(OutlineColorUniformName, unfocusedColor);

        textInput.MouseDirectOverlapEnter += GotFocus;
        textInput.MouseDirectOverlapLeave += LostFocus;
        textInput.Property = property;
        textInput.Targets = targets;

        textComp.FontSize = fontSize ?? EditorUI.Styles.PropertyInputFontSize;
        textComp.Color = EditorUI.Styles.PropertyInputTextColor;
        textComp.HorizontalAlignment = EHorizontalAlignment.Left;
        textComp.VerticalAlignment = EVerticalAlignment.Center;
        textComp.BoundableTransform.Margins = textMargins ?? new Vector4(5.0f, 2.0f, 5.0f, 2.0f);
        textComp.ClipToBounds = true;
        textComp.WrapMode = FontGlyphSet.EWrapMode.None;

        textDriver.Property = property;
        textDriver.Sources = targets;

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
        var material = CreateOutlineMaterial()!;
        material.SetVector4(FillColorUniformName, ColorF4.Black);
        material.SetVector4(OutlineColorUniformName, ColorF4.Gray);
        materialComponent.Material = material;

        var tfm = materialComponent.BoundableTransform;
        tfm.MinAnchor = new Vector2(0.0f, 0.0f);
        tfm.MaxAnchor = new Vector2(0.0f, 0.0f);
        tfm.Width = size;
        tfm.Height = size;

        parent.NewChild(out UIToggleComponent toggle);
        toggle.Property = property;
        toggle.Targets = targets;

        parent.NewChild(out UITextComponent glyph, "ToggleLabel");
        glyph.Text = string.Empty;
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
            material.SetVector4(OutlineColorUniformName, ColorF4.White);
            glyph.Color = ColorF4.White;
        }

        void LostFocus(UIInteractableComponent _)
        {
            material.SetVector4(OutlineColorUniformName, ColorF4.Gray);
            glyph.Color = ColorF4.Gray;
        }

        toggle.OnStateChanged += SetCheckState;
        toggle.MouseDirectOverlapEnter += GotFocus;
        toggle.MouseDirectOverlapLeave += LostFocus;
        SetCheckState(toggle.CurrentState);

        return (toggle, materialComponent, glyph);
    }

    private static XRMaterial? CreateOutlineMaterial()
    {
        string fragCode = @"
            #version 460

            layout (location = 4) in vec2 FragUV0;
            out vec4 FragColor;

            uniform float OutlineWidth;
            uniform vec4 OutlineColor;
            uniform vec4 FillColor;
            uniform float UIWidth;
            uniform float UIHeight;

            void main()
            {
                float pixelOutlineWidthX = OutlineWidth / UIWidth;
                float pixelOutlineWidthY = OutlineWidth / UIHeight;
                float isOutline = max(
                    step(FragUV0.x, pixelOutlineWidthX) + step(1.0 - pixelOutlineWidthX, FragUV0.x),
                    step(FragUV0.y, pixelOutlineWidthY) + step(1.0 - pixelOutlineWidthY, FragUV0.y));
                FragColor = mix(FillColor, OutlineColor, isOutline);
            }";
        XRShader frag = new(EShaderType.Fragment, fragCode);
        ShaderVar[] parameters =
        [
            new ShaderFloat(2.0f, OutlineWidthUniformName),
            new ShaderVector4(ColorF4.Transparent, OutlineColorUniformName),
            new ShaderVector4(ColorF4.Transparent, FillColorUniformName),
        ];
        var mat = new XRMaterial(parameters, frag);
        mat.EnableTransparency();
        return mat;
    }
}
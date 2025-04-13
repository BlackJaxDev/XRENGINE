using System.ComponentModel;
using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Text;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// Attribute to specify a custom editor for a class object.
/// </summary>
/// <param name="editorType"></param>
[AttributeUsage(AttributeTargets.Class)]
public abstract class EditorComponentAttribute : Attribute
{
    public abstract void CreateEditor(SceneNode node, PropertyInfo prop, object?[]? objects);
}

public class InspectorPanel : EditorPanel
{
    private const string OutlineColorUniformName = "OutlineColor";

    private object?[]? _inspectedObjects;
    public object?[]? InspectedObjects
    {
        get => _inspectedObjects;
        set => SetField(ref _inspectedObjects, value);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(InspectedObjects):
                RemakeChildren();
                break;
        }
    }

    private bool _isLocked = true;
    public bool IsLocked
    {
        get => _isLocked;
        set => SetField(ref _isLocked, value);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RemakeChildren();
        Selection.SelectionChanged += OnSelectionChanged;
    }

    protected override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        SceneNode.Transform.Clear();
        Selection.SelectionChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged(SceneNode[] obj)
    {
        if (!IsLocked)
            InspectedObjects = obj;
    }

    private void RemakeChildren()
    {
        SceneNode.Transform.Clear();
        CreatePropertyList(SceneNode, this);
    }

    /// <summary>
    /// Get the properties that are common to all the inspected objects.
    /// </summary>
    /// <returns></returns>
    private static List<PropertyInfo> GetMatchingProperties(object?[]? objects)
    {
        List<PropertyInfo> matching = [];
        if (objects is null)
            return matching;
        
        bool first = true;
        foreach (var obj in objects)
        {
            if (obj is null)
                continue;

            var props = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (prop is null || prop.GetIndexParameters().Length > 0)
                    continue;

                if (first)
                    matching.Add(prop);
                else
                    matching = [.. matching.Intersect([prop])];
            }
            first = false;
        }
        return matching;
    }

    public float Margin { get; set; } = 4.0f;

    private void CreatePropertyList(SceneNode parentNode, InspectorPanel inspectorPanel)
    {
        var listNode = parentNode.NewChild<UIMaterialComponent>(out var menuMat);
        menuMat.Material = MakeBackgroundMaterial();
        var listTfm = listNode.SetTransform<UIListTransform>();
        listTfm.DisplayHorizontal = false;
        listTfm.ItemSpacing = 0.0f;
        listTfm.Padding = new Vector4(0.0f);
        listTfm.ItemAlignment = EListAlignment.TopOrLeft;
        listTfm.ItemSize = null;
        listTfm.Width = 150;
        listTfm.Height = null;
        Properties = CreateNodes(listNode, InspectedObjects);
    }

    public List<PropertyInfo>? Properties { get; private set; }

    private static List<PropertyInfo> CreateNodes(SceneNode listNode, object?[]? inspectedObjects)
    {
        float fontSize = EditorUI.Styles.PropertyNameFontSize;
        float leftMargin = 5.0f;
        float rightMargin = 5.0f;
        float verticalSpacing = 2.0f;

        List<PropertyInfo> props = GetMatchingProperties(inspectedObjects);
        float textWidth = MeasureTextWidth(fontSize, props);
        foreach (var prop in props)
            CreatePropertyDisplay(listNode, inspectedObjects, fontSize, leftMargin, rightMargin, verticalSpacing, textWidth, prop);
        return props;
    }

    private static float MeasureTextWidth(float fontSize, List<PropertyInfo> matching)
    {
        float textWidth = 0.0f;
        foreach (var prop in matching)
            textWidth = Math.Max(textWidth, UITextComponent.MeasureWidth(ResolveName(prop) ?? string.Empty, FontGlyphSet.LoadDefaultFont(), fontSize));
        return textWidth;
    }

    private static void CreatePropertyDisplay(
        SceneNode listNode,
        object?[]? inspectedObjects,
        float fontSize,
        float leftMargin,
        float rightMargin,
        float verticalSpacing,
        float textWidth,
        PropertyInfo prop)
    {
        var n = listNode.NewChild();
        var splitter = n.SetTransform<UIDualSplitTransform>();
        var nameNode = n.NewChild<UITextComponent>(out var nameText);
        var valueNode = n.NewChild();

        splitter.FixedSize = textWidth + leftMargin + rightMargin;
        splitter.VerticalSplit = false;
        splitter.FirstFixedSize = true;
        splitter.SplitPercent = 0.5f;

        nameText.Text = ResolveName(prop);
        nameText.FontSize = fontSize;
        nameText.HorizontalAlignment = EHorizontalAlignment.Right;
        nameText.VerticalAlignment = EVerticalAlignment.Center;
        nameText.Color = EditorUI.Styles.PropertyNameTextColor;
        var nameTfm = nameText.BoundableTransform;
        nameTfm.Margins = new Vector4(leftMargin, verticalSpacing, rightMargin, verticalSpacing);

        CreatePropertyEditor(prop.PropertyType)?.Invoke(valueNode, prop, inspectedObjects);
    }

    private static string? ResolveName(PropertyInfo prop)
    {
        var name = prop.Name;
        var attr = prop.GetCustomAttribute<DisplayNameAttribute>();
        if (attr is not null)
            name = attr.DisplayName;
        return CamelCaseWithSpaces(name);
    }

    private static string? CamelCaseWithSpaces(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // Insert space between lowercase or digit and uppercase letter
        string withSpaces = System.Text.RegularExpressions.Regex.Replace(
            name,
            "(?<=[a-z0-9])([A-Z])",
            " $1");

        // Handle sequences like "XMLHttpRequest" -> "XML Http Request"
        withSpaces = System.Text.RegularExpressions.Regex.Replace(
            withSpaces,
            "([A-Z])([A-Z][a-z])",
            "$1 $2");

        return withSpaces;
    }

    public static Action<SceneNode, PropertyInfo, object?[]?>? CreatePropertyEditor(Type propType)
        => Type.GetTypeCode(propType) switch
        {
            TypeCode.Object => CreateObjectEditor(propType),
            TypeCode.Boolean => CreateBooleanEditor(),
            TypeCode.Char => CreateCharEditor(),
            TypeCode.SByte => CreateSByteEditor(),
            TypeCode.Byte => CreateByteEditor(),
            TypeCode.Int16 => CreateInt16Editor(),
            TypeCode.UInt16 => CreateUInt16Editor(),
            TypeCode.Int32 => CreateInt32Editor(),
            TypeCode.UInt32 => CreateUInt32Editor(),
            TypeCode.Int64 => CreateInt64Editor(),
            TypeCode.UInt64 => CreateUInt64Editor(),
            TypeCode.Single => CreateSingleEditor(),
            TypeCode.Double => CreateDoubleEditor(),
            TypeCode.Decimal => CreateDecimalEditor(),
            TypeCode.DateTime => CreateDateTimeEditor(),
            TypeCode.String => CreateStringEditor(),
            _ => null,
        };

    public static XRMaterial? CreateUITextInputMaterial()
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
        XRShader frag = new(EShaderType.Fragment, TextFile.FromText(fragCode));
        ShaderVar[] parameters =
        [
            new ShaderFloat(2.0f, "OutlineWidth"),
            new ShaderVector4(ColorF4.Transparent, OutlineColorUniformName),
        ];
        var mat = new XRMaterial(parameters, frag);
        mat.EnableTransparency();
        return mat;
    }

    private static T TextEditor<T>(SceneNode n, PropertyInfo prop, object?[]? objects) where T : UITextInputComponent
    {
        var matComp = n.AddComponent<UIMaterialComponent>()!;
        var mat = CreateUITextInputMaterial()!;
        //mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        matComp!.Material = mat;

        n.NewChild<UITextComponent, T, UIPropertyTextDriverComponent>(out var textComp, out var textInput, out var textDriver);
        void GotFocus(UIInteractableComponent comp) => mat.SetVector4(OutlineColorUniformName, ColorF4.White);
        void LostFocus(UIInteractableComponent comp) => mat.SetVector4(OutlineColorUniformName, ColorF4.Transparent);
        textInput.MouseDirectOverlapEnter += GotFocus;
        textInput.MouseDirectOverlapLeave += LostFocus;
        textInput.Property = prop;
        textInput.Targets = objects;
        
        textComp!.FontSize = EditorUI.Styles.PropertyInputFontSize;
        textComp.Color = EditorUI.Styles.PropertyInputTextColor;
        textComp.HorizontalAlignment = EHorizontalAlignment.Left;
        textComp.VerticalAlignment = EVerticalAlignment.Center;
        textComp.BoundableTransform.Margins = new Vector4(5.0f, 2.0f, 5.0f, 2.0f);
        textComp.ClipToBounds = true;
        textComp.WrapMode = FontGlyphSet.EWrapMode.None;

        textDriver!.Property = prop;
        textDriver.Sources = objects;

        return textInput;
    }
    private static T ToggleEditor<T>(SceneNode n, PropertyInfo prop, object?[]? objects) where T : UITextInputComponent
    {
        var matComp = n.AddComponent<UIMaterialComponent>()!;
        var mat = CreateUITextInputMaterial()!;
        //mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        matComp!.Material = mat;

        n.NewChild<UITextComponent, T, UIPropertyTextDriverComponent>(out var textComp, out var textInput, out var textDriver);
        void GotFocus(UIInteractableComponent comp) => mat.SetVector4(OutlineColorUniformName, ColorF4.White);
        void LostFocus(UIInteractableComponent comp) => mat.SetVector4(OutlineColorUniformName, ColorF4.Transparent);
        textInput.MouseDirectOverlapEnter += GotFocus;
        textInput.MouseDirectOverlapLeave += LostFocus;
        textInput.Property = prop;
        textInput.Targets = objects;

        textComp!.FontSize = EditorUI.Styles.PropertyInputFontSize;
        textComp.Color = EditorUI.Styles.PropertyInputTextColor;
        textComp.HorizontalAlignment = EHorizontalAlignment.Left;
        textComp.VerticalAlignment = EVerticalAlignment.Center;
        textComp.BoundableTransform.Margins = new Vector4(5.0f, 2.0f, 5.0f, 2.0f);
        textComp.ClipToBounds = true;
        textComp.WrapMode = FontGlyphSet.EWrapMode.None;

        textDriver!.Property = prop;
        textDriver.Sources = objects;

        return textInput;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateStringEditor()
    {
        static void StringEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            var text = TextEditor<UITextInputComponent>(n, prop, objects);
        }
        return StringEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateDateTimeEditor()
    {
        static void DateTimeEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            var text = TextEditor<UITextInputComponent>(n, prop, objects);
        }
        return DateTimeEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateDecimalEditor()
    {
        static void DecimalEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UIDecimalInputComponent>(n, prop, objects);
        }
        return DecimalEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateDoubleEditor()
    {
        static void DoubleEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UIDoubleInputComponent>(n, prop, objects);
        }
        return DoubleEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateSingleEditor()
    {
        static void SingleEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UIFloatInputComponent>(n, prop, objects);
        }
        return SingleEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateUInt64Editor()
    {
        static void UInt64Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UIULongInputComponent>(n, prop, objects);
        }
        return UInt64Editor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateInt64Editor()
    {
        static void Int64Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UILongInputComponent>(n, prop, objects);
        }
        return Int64Editor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateUInt32Editor()
    {
        static void UInt32Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UIUIntInputComponent>(n, prop, objects);
        }
        return UInt32Editor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateInt32Editor()
    {
        static void Int32Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UIIntInputComponent>(n, prop, objects);
        }
        return Int32Editor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateUInt16Editor()
    {
        static void UInt16Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UIUShortInputComponent>(n, prop, objects);
        }
        return UInt16Editor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateInt16Editor()
    {
        static void Int16Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UIShortInputComponent>(n, prop, objects);
        }
        return Int16Editor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateByteEditor()
    {
        static void ByteEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UIByteInputComponent>(n, prop, objects);
        }
        return ByteEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateSByteEditor()
    {
        static void SByteEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            TextEditor<UISByteInputComponent>(n, prop, objects);
        }
        return SByteEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateCharEditor()
    {
        static void CharEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            var text = TextEditor<UITextInputComponent>(n, prop, objects);
            text.MaxInputLength = 1;
        }
        return CharEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?> CreateBooleanEditor()
    {
        static void BooleanEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            var text = ToggleEditor<UITextInputComponent>(n, prop, objects);
        }
        return BooleanEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateObjectEditor(Type propType)
    {
        switch (propType)
        {
            case Type t when t == typeof(Vector2):
                return CreateVector2Editor;
            case Type t when t == typeof(Vector3):
                return CreateVector3Editor;
            case Type t when t == typeof(Vector4):
                return CreateVector4Editor;
            case Type t when t == typeof (Quaternion):
                return CreateQuaternionEditor;
            case Type t when t == typeof(Color):
                return CreateColorEditor;
            case Type t when t == typeof(ColorF3):
                return CreateColorEditor;
            case Type t when t == typeof(ColorF4):
                return CreateColorEditor;
            default:
                {
                    if (propType.IsEnum)
                        return CreateEnumEditor(propType);
                    else if (propType.IsArray)
                        return CreateArrayEditor(propType);
                    else if (propType.IsGenericType)
                        return CreateGenericEditor(propType);
                    else
                        return CreateClassEditor(propType);
                }
        }
    }

    private static void CreateQuaternionEditor(SceneNode node, PropertyInfo info, object?[]? arg3)
    {
        //Quaternion editor is just a Vector3 yaw, pitch, roll editor
    }

    private static void CreateVector4Editor(SceneNode node, PropertyInfo info, object?[]? arg3)
    {

    }

    private static void CreateVector3Editor(SceneNode node, PropertyInfo info, object?[]? arg3)
    {

    }

    private static void CreateVector2Editor(SceneNode node, PropertyInfo info, object?[]? arg3)
    {

    }

    private static void CreateColorEditor(SceneNode node, PropertyInfo info, object?[]? arg3)
    {

    }

    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateClassEditor(Type propType)
        => propType.GetCustomAttribute<EditorComponentAttribute>() is EditorComponentAttribute attr
            ? attr.CreateEditor
            : CreateDefaultEditor(propType);

    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateGenericEditor(Type propType)
    {
        static void GenericEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            var text = TextEditor<UITextInputComponent>(n, prop, objects);
        }
        return GenericEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateArrayEditor(Type propType)
    {
        static void ArrayEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            var text = TextEditor<UITextInputComponent>(n, prop, objects);
        }
        return ArrayEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateEnumEditor(Type propType)
    {
        static void EnumEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            var text = TextEditor<UITextInputComponent>(n, prop, objects);
        }
        return EnumEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateDefaultEditor(Type propType)
    {
        static void DefaultEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            Debug.Out($"No editor for type {prop.PropertyType}");
            var text = TextEditor<UITextInputComponent>(n, prop, objects);
        }
        return DefaultEditor;
    }
}

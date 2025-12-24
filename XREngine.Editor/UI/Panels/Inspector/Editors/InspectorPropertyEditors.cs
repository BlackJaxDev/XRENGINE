using System.Drawing;
using System.Numerics;
using System.Reflection;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    private const string OutlineColorUniformName = "OutlineColor";
    private const string OutlineWidthUniformName = "OutlineWidth";
    private const string FillColorUniformName = "FillColor";

    public static Action<SceneNode, PropertyInfo, object?[]?>? CreateNew(Type propType)
        => Type.GetTypeCode(propType) switch
        {
            TypeCode.Object => CreateObjectEditor(propType),
            TypeCode.Boolean => PrimitiveTypes.CreateBooleanEditor(),
            TypeCode.Char => PrimitiveTypes.CreateCharEditor(),
            TypeCode.SByte => PrimitiveTypes.CreateSByteEditor(),
            TypeCode.Byte => PrimitiveTypes.CreateByteEditor(),
            TypeCode.Int16 => PrimitiveTypes.CreateInt16Editor(),
            TypeCode.UInt16 => PrimitiveTypes.CreateUInt16Editor(),
            TypeCode.Int32 => PrimitiveTypes.CreateInt32Editor(),
            TypeCode.UInt32 => PrimitiveTypes.CreateUInt32Editor(),
            TypeCode.Int64 => PrimitiveTypes.CreateInt64Editor(),
            TypeCode.UInt64 => PrimitiveTypes.CreateUInt64Editor(),
            TypeCode.Single => PrimitiveTypes.CreateSingleEditor(),
            TypeCode.Double => PrimitiveTypes.CreateDoubleEditor(),
            TypeCode.Decimal => PrimitiveTypes.CreateDecimalEditor(),
            TypeCode.DateTime => PrimitiveTypes.CreateDateTimeEditor(),
            TypeCode.String => PrimitiveTypes.CreateStringEditor(),
            _ => null,
        };

    public static XRMaterial? CreateUIOutlineMaterial()
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

    private static T TextEditor<T>(SceneNode n, PropertyInfo prop, object?[]? objects) where T : UITextInputComponent
    {
        var matComp = n.AddComponent<UIMaterialComponent>()!;
        var mat = CreateUIOutlineMaterial()!;
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
    private static T ObjectSelector<T>(SceneNode n, PropertyInfo prop, object?[]? objects) where T : UITextInputComponent
    {
        var matComp = n.AddComponent<UIMaterialComponent>()!;
        var mat = CreateUIOutlineMaterial()!;
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

    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateObjectEditor(Type propType)
    {
        switch (propType)
        {
            case Type t when t == typeof(Vector2):
                return EngineTypes.CreateVector2Editor;
            case Type t when t == typeof(Vector3):
                return EngineTypes.CreateVector3Editor;
            case Type t when t == typeof(Vector4):
                return EngineTypes.CreateVector4Editor;
            case Type t when t == typeof(Quaternion):
                return EngineTypes.CreateQuaternionEditor;
            case Type t1 when t1 == typeof(Color):
            case Type t2 when t2 == typeof(ColorF3):
            case Type t3 when t3 == typeof(ColorF4):
                return EngineTypes.CreateColorEditor;
            case Type t when t == typeof(PropAnimFloat):
                return EngineTypes.CreatePropAnimFloatEditor;
            case Type t when t == typeof(LayerMask):
                return EngineTypes.CreateLayerMaskEditor;
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

    /// <summary>
    /// This method creates an editor for a class type property.
    /// It checks for a custom attribute `EditorComponentAttribute` on the class type,
    /// and if found, it uses the `CreateEditor` method from that attribute to create the editor.
    /// Otherwise, it defaults to creating an object selector editor for the class type.
    /// </summary>
    /// <param name="propType"></param>
    /// <returns></returns>
    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateClassEditor(Type propType)
    {
        if (propType.GetCustomAttribute<EditorComponentAttribute>() is EditorComponentAttribute attr)
            return attr.CreateEditor;

        if (typeof(OpenGLRenderer.GLObjectBase).IsAssignableFrom(propType))
            return CreateGlObjectEditor(propType);

        if (typeof(XRAsset).IsAssignableFrom(propType))
            return CreateXRAssetEditor(propType);

        return CreateObjectSelector(propType);
    }

    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateGenericEditor(Type propType)
    {
        if (CollectionTypes.TryCreateEditor(propType, out var collectionEditor))
            return collectionEditor;

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

    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateObjectSelector(Type propType)
    {
        static void ObjectSelector(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            var selector = ObjectSelector<UITextInputComponent>(n, prop, objects);
        }
        return ObjectSelector;
    }
}

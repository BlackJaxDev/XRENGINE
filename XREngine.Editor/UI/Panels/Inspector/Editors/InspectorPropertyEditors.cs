using System.Drawing;
using System.Numerics;
using System.Reflection;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{

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

    private static T TextEditor<T>(SceneNode n, PropertyInfo prop, object?[]? objects) where T : UITextInputComponent
    {
        var result = NativeUIElements.CreateTextInputField<T>(n, prop, objects);
        return result.Input;
    }
    private static T ObjectSelector<T>(SceneNode n, PropertyInfo prop, object?[]? objects) where T : UITextInputComponent
    {
        var result = NativeUIElements.CreateTextInputField<T>(n, prop, objects);
        return result.Input;
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
        if (CollectionTypes.TryCreateEditor(propType, out var collectionEditor))
            return collectionEditor;
        static void ArrayEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            var text = TextEditor<UITextInputComponent>(n, prop, objects);
        }
        return ArrayEditor;
    }

    private static Action<SceneNode, PropertyInfo, object?[]?>? CreateEnumEditor(Type propType)
    {
        if (propType.IsDefined(typeof(FlagsAttribute), inherit: false))
            return static (node, property, targets) => TextEditor<UITextInputComponent>(node, property, targets);
        static void EnumEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
        {
            var binding = new EditorEnumPropertyBinding(prop, objects ?? []);
            var combo = n.AddComponent<UIComboBoxComponent>()!;
            var popup = n.AddComponent<NativePropertyPopupHost>()!;
            combo.Popup = popup.CreatePopup();
            combo.VirtualOptions = binding;
            combo.Binding = binding;
            combo.IsReadOnly = !prop.CanWrite;
            combo.AccessibilityLabel = prop.Name;
            n.AddComponent<UIMaterialComponent>()!.SetBatchBorderStyle(ColorF4.Transparent, ColorF4.Gray, 1);
            n.NewChild<UITextComponent>(out var label);
            label.Text = combo.AccessibilityValue ?? "Mixed or undefined";
            label.VerticalAlignment = EVerticalAlignment.Center;
            combo.SelectionChanged += _ => label.Text = combo.AccessibilityValue ?? "Mixed or undefined";
            combo.Opening += _ => combo.PopupPosition = popup.GetPosition();
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

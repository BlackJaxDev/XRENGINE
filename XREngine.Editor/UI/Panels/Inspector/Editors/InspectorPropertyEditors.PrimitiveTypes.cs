using System.Numerics;
using System.Reflection;
using XREngine.Data.Colors;
using XREngine.Rendering.UI;
using XREngine.Scene;
using static XREngine.Rendering.UI.UIToggleComponent;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static class PrimitiveTypes
    {
        public static Action<SceneNode, PropertyInfo, object?[]?> CreateStringEditor()
        {
            static void StringEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                var text = TextEditor<UITextInputComponent>(n, prop, objects);
            }
            return StringEditor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateDateTimeEditor()
            => (n, prop, objects) =>
            {
                var matComp = n.AddComponent<UIMaterialComponent>()!;
                var mat = CreateUIOutlineMaterial()!;
                //mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
                matComp!.Material = mat;
                n.NewChild(out UIDateTimeInputComponent dateTimeInput);
                void GotFocus(UIInteractableComponent comp) => mat.SetVector4(OutlineColorUniformName, ColorF4.White);
                void LostFocus(UIInteractableComponent comp) => mat.SetVector4(OutlineColorUniformName, ColorF4.Gray);
                dateTimeInput.MouseDirectOverlapEnter += GotFocus;
                dateTimeInput.MouseDirectOverlapLeave += LostFocus;
                dateTimeInput.Property = prop;
                dateTimeInput.Targets = objects;
            };

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateDecimalEditor()
        {
            static void DecimalEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UIDecimalInputComponent>(n, prop, objects);
            }
            return DecimalEditor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateDoubleEditor()
        {
            static void DoubleEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UIDoubleInputComponent>(n, prop, objects);
            }
            return DoubleEditor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateSingleEditor()
        {
            static void SingleEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UIFloatInputComponent>(n, prop, objects);
            }
            return SingleEditor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateUInt64Editor()
        {
            static void UInt64Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UIULongInputComponent>(n, prop, objects);
            }
            return UInt64Editor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateInt64Editor()
        {
            static void Int64Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UILongInputComponent>(n, prop, objects);
            }
            return Int64Editor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateUInt32Editor()
        {
            static void UInt32Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UIUIntInputComponent>(n, prop, objects);
            }
            return UInt32Editor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateInt32Editor()
        {
            static void Int32Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UIIntInputComponent>(n, prop, objects);
            }
            return Int32Editor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateUInt16Editor()
        {
            static void UInt16Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UIUShortInputComponent>(n, prop, objects);
            }
            return UInt16Editor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateInt16Editor()
        {
            static void Int16Editor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UIShortInputComponent>(n, prop, objects);
            }
            return Int16Editor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateByteEditor()
        {
            static void ByteEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UIByteInputComponent>(n, prop, objects);
            }
            return ByteEditor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateSByteEditor()
        {
            static void SByteEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                TextEditor<UISByteInputComponent>(n, prop, objects);
            }
            return SByteEditor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateCharEditor()
        {
            static void CharEditor(SceneNode n, PropertyInfo prop, object?[]? objects)
            {
                var text = TextEditor<UITextInputComponent>(n, prop, objects);
                text.MaxInputLength = 1;
            }
            return CharEditor;
        }

        public static Action<SceneNode, PropertyInfo, object?[]?> CreateBooleanEditor()
            => (n, prop, objects) =>
            {
                var matComp = n.AddComponent<UIMaterialComponent>()!;
                var mat = CreateUIOutlineMaterial()!;
                mat.SetVector4(FillColorUniformName, ColorF4.Black);
                //mat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
                matComp!.Material = mat;

                var tfm = matComp.BoundableTransform;
                tfm.MinAnchor = new Vector2(0.0f, 0.0f);
                tfm.MaxAnchor = new Vector2(0.0f, 0.0f);
                tfm.Width = 20.0f;
                tfm.Height = 20.0f;

                n.NewChild(out UIToggleComponent toggleInput);
                toggleInput.Property = prop;
                toggleInput.Targets = objects;

                n.NewChild(out UITextComponent textComp, "ToggleLabel");
                textComp.Text = "";
                textComp.FontSize = 16;
                textComp.Color = ColorF4.White;
                textComp.HorizontalAlignment = EHorizontalAlignment.Center;
                textComp.VerticalAlignment = EVerticalAlignment.Center;

                toggleInput.OnStateChanged += SetCheckState;
                toggleInput.MouseDirectOverlapEnter += GotFocus;
                toggleInput.MouseDirectOverlapLeave += LostFocus;

                void GotFocus(UIInteractableComponent comp)
                {
                    mat.SetVector4(OutlineColorUniformName, ColorF4.White);
                    textComp.Color = ColorF4.White;
                }

                void LostFocus(UIInteractableComponent comp)
                {
                    mat.SetVector4(OutlineColorUniformName, ColorF4.Gray);
                    textComp.Color = ColorF4.Gray;
                }

                void SetCheckState(ECurrentState state)
                {
                    textComp.Text = state switch
                    {
                        ECurrentState.True => "?",
                        ECurrentState.Intermediate => "¦",
                        _ => "",
                    };
                }
            };
    }
}

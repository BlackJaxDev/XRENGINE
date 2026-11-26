using System.Numerics;
using System.Reflection;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static partial class EngineTypes
    {
        public static void CreateQuaternionEditor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out QuaternionTransformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(QuaternionTransformer.Yaw)), transformer.GetTransformedProperty(nameof(QuaternionTransformer.Yaw)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(QuaternionTransformer.Pitch)), transformer.GetTransformedProperty(nameof(QuaternionTransformer.Pitch)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(QuaternionTransformer.Roll)), transformer.GetTransformedProperty(nameof(QuaternionTransformer.Roll)), tfmObj);
            SpreadFields(node);
        }

        /// <summary>Creates pitch, yaw, and roll fields in degrees for a <see cref="XREngine.Data.Transforms.Rotations.Rotator"/>.</summary>
        public static void CreateRotatorEditor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out RotatorTransformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(RotatorTransformer.Pitch)), transformer.GetTransformedProperty(nameof(RotatorTransformer.Pitch)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(RotatorTransformer.Yaw)), transformer.GetTransformedProperty(nameof(RotatorTransformer.Yaw)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(RotatorTransformer.Roll)), transformer.GetTransformedProperty(nameof(RotatorTransformer.Roll)), tfmObj);
            SpreadFields(node);
        }

        public static void CreateVector4Editor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out Vector4Transformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector4Transformer.X)), transformer.GetTransformedProperty(nameof(Vector4Transformer.X)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector4Transformer.Y)), transformer.GetTransformedProperty(nameof(Vector4Transformer.Y)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector4Transformer.Z)), transformer.GetTransformedProperty(nameof(Vector4Transformer.Z)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector4Transformer.W)), transformer.GetTransformedProperty(nameof(Vector4Transformer.W)), tfmObj);
            SpreadFields(node);
        }

        public static void CreateVector3Editor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out Vector3Transformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector3Transformer.X)), transformer.GetTransformedProperty(nameof(Vector3Transformer.X)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector3Transformer.Y)), transformer.GetTransformedProperty(nameof(Vector3Transformer.Y)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector3Transformer.Z)), transformer.GetTransformedProperty(nameof(Vector3Transformer.Z)), tfmObj);
            SpreadFields(node);

        }

        public static void CreateVector2Editor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out Vector2Transformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector2Transformer.X)), transformer.GetTransformedProperty(nameof(Vector2Transformer.X)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector2Transformer.Y)), transformer.GetTransformedProperty(nameof(Vector2Transformer.Y)), tfmObj);
            SpreadFields(node);
        }

        public static void CreateLayerMaskEditor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out LayerMaskTransformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateInt32Editor()(node.NewChild(nameof(LayerMaskTransformer.Value)), transformer.GetTransformedProperty(nameof(LayerMaskTransformer.Value)), tfmObj);
            SpreadFields(node);
        }

        /// <summary>
        /// Gives each field of a field-list editor an equal share of the row width, like the X, Y, and Z boxes
        /// of a web form. Each field stretches between fraction anchors, and the margins leave a small gap.
        /// </summary>
        private static void SpreadFields(SceneNode node)
        {
            const float halfGap = 2.0f;
            TransformBase parent = node.Transform;
            int count = parent.Children.Count;
            for (int index = 0; index < count; index++)
            {
                if (parent.Children[index] is not UIBoundableTransform field)
                    continue;
                field.MinAnchor = new Vector2(index / (float)count, 0.0f);
                field.MaxAnchor = new Vector2((index + 1) / (float)count, 1.0f);
                field.NormalizedPivot = Vector2.Zero;
                field.Margins = new Vector4(index == 0 ? 0.0f : halfGap, 0.0f, index == count - 1 ? 0.0f : halfGap, 0.0f);
            }
        }

        private static void InitListArrangement<T>(SceneNode node, PropertyInfo info, object?[]? objects, out T transformer, out object?[] tfmObj) where T : DataTransformerBase
        {
            // The fields share the row width equally (see SpreadFields), so a plain bounded transform holds them.
            var tfm = node.SetTransform<UIBoundableTransform>();
            tfm.Height = 25.0f;
            transformer = node.AddComponent<T>()!;
            transformer.Property = info;
            transformer.Targets = objects;
            tfmObj = [transformer];
        }

        public static void CreateColorEditor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            var color = node.AddComponent<UIColorSelectorComponent>()!;
            node.AddComponent<UIMaterialComponent>()!.SetBatchBorderStyle(ColorF4.White, ColorF4.Gray, 1);
            var popup = node.AddComponent<NativePropertyPopupHost>()!;
            color.Popup = popup.CreatePopup();
            color.EditingStarted += _ => color.PopupPosition = popup.GetPosition();
            color.Binding = new EditorColorPropertyBinding(info, objects ?? []);
            color.IsReadOnly = !info.CanWrite;
            color.AccessibilityLabel = info.Name;
        }

        public static void CreatePropAnimFloatEditor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            CreateFloatAnimationDetail(node, info, objects);
        }
    }
}

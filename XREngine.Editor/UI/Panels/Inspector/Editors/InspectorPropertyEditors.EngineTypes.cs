using System.Reflection;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static class EngineTypes
    {
        public static void CreateQuaternionEditor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out QuaternionTransformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(QuaternionTransformer.Yaw)), transformer.GetTransformedProperty(nameof(QuaternionTransformer.Yaw)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(QuaternionTransformer.Pitch)), transformer.GetTransformedProperty(nameof(QuaternionTransformer.Pitch)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(QuaternionTransformer.Roll)), transformer.GetTransformedProperty(nameof(QuaternionTransformer.Roll)), tfmObj);
        }

        public static void CreateVector4Editor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out Vector4Transformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector4Transformer.X)), transformer.GetTransformedProperty(nameof(Vector4Transformer.X)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector4Transformer.Y)), transformer.GetTransformedProperty(nameof(Vector4Transformer.Y)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector4Transformer.Z)), transformer.GetTransformedProperty(nameof(Vector4Transformer.Z)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector4Transformer.W)), transformer.GetTransformedProperty(nameof(Vector4Transformer.W)), tfmObj);
        }

        public static void CreateVector3Editor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out Vector3Transformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector3Transformer.X)), transformer.GetTransformedProperty(nameof(Vector3Transformer.X)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector3Transformer.Y)), transformer.GetTransformedProperty(nameof(Vector3Transformer.Y)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector3Transformer.Z)), transformer.GetTransformedProperty(nameof(Vector3Transformer.Z)), tfmObj);

        }

        public static void CreateVector2Editor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out Vector2Transformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector2Transformer.X)), transformer.GetTransformedProperty(nameof(Vector2Transformer.X)), tfmObj);
            PrimitiveTypes.CreateSingleEditor()(node.NewChild(nameof(Vector2Transformer.Y)), transformer.GetTransformedProperty(nameof(Vector2Transformer.Y)), tfmObj);
        }

        public static void CreateLayerMaskEditor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            InitListArrangement(node, info, objects, out LayerMaskTransformer transformer, out object?[] tfmObj);
            PrimitiveTypes.CreateInt32Editor()(node.NewChild(nameof(LayerMaskTransformer.Value)), transformer.GetTransformedProperty(nameof(LayerMaskTransformer.Value)), tfmObj);
        }

        private static void InitListArrangement<T>(SceneNode node, PropertyInfo info, object?[]? objects, out T transformer, out object?[] tfmObj) where T : DataTransformerBase
        {
            var tfm = node.SetTransform<UIListTransform>();
            tfm.DisplayHorizontal = true;
            tfm.Height = 25.0f;
            tfm.ItemAlignment = EListAlignment.TopOrLeft;
            tfm.ItemSpacing = 5.0f;
            tfm.ItemSize = null;
            transformer = node.AddComponent<T>()!;
            transformer.Property = info;
            transformer.Targets = objects;
            tfmObj = [transformer];
        }

        public static void CreateColorEditor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            //Render a box with the color.
            //On click, open a color picker dialog.
        }

        public static void CreatePropAnimFloatEditor(SceneNode node, PropertyInfo info, object?[]? objects)
        {
            //Render a box with the spline curve.
            //On click, open a spline editor dialog.
        }
    }
}

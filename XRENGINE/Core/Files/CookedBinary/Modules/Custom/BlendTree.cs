using XREngine.Animation;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class BlendTreeCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(1400, "BlendTrees", "BlendTree custom payload via serialized blend tree models.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (!allowCustom || value is not BlendTree blendTree || !XREngine.BlendTreeCookedBinarySerializer.CanHandle(runtimeType))
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.CustomObject);
            WriteTypeName(writer, runtimeType);
            XREngine.BlendTreeCookedBinarySerializer.Write(writer, blendTree);
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.CustomObject)
            {
                value = null;
                return false;
            }

            long rewind = reader.Position;
            string typeName = reader.ReadString();
            Type targetType = TryResolveSerializedTypeName(typeName) ?? expectedType ?? typeof(BlendTree1D);
            if (!XREngine.BlendTreeCookedBinarySerializer.CanHandle(targetType))
            {
                reader.Position = rewind;
                value = null;
                return false;
            }

            value = XREngine.BlendTreeCookedBinarySerializer.Read(targetType, reader);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (!allowCustom || value is not BlendTree blendTree || !XREngine.BlendTreeCookedBinarySerializer.CanHandle(runtimeType))
                return false;

            calculator.AddBytes(SizeOfTypeName(runtimeType) + XREngine.BlendTreeCookedBinarySerializer.CalculateSize(blendTree));
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (!allowCustom || value is not BlendTree blendTree || !XREngine.BlendTreeCookedBinarySerializer.CanHandle(runtimeType))
                return null;

            object model = BlendTreeSerialization.CreateModel(blendTree);
            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddExpandedCustomModelValueNode(node, runtimeType, model, model.GetType(), "BlendTree custom serializer writes a serialized blend tree model via WriteValue");
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!allowCustom || !typeof(BlendTree).IsAssignableFrom(type) || !XREngine.BlendTreeCookedBinarySerializer.CanHandle(type))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddExpandedCustomModelSchemaNode(node, type, GetBlendTreeSerializedModelType(type), "BlendTree custom serializer writes a serialized blend tree model via WriteValue");
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}
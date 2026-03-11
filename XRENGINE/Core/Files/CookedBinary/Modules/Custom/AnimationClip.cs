using XREngine.Animation;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class AnimationClipCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(1300, "AnimationClip", "AnimationClip custom payload via AnimationClipSerializedModel.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (!allowCustom || value is not AnimationClip animationClip)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.CustomObject);
            WriteTypeName(writer, runtimeType);
            XREngine.AnimationClipCookedBinarySerializer.Write(writer, animationClip);
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
            Type targetType = TryResolveSerializedTypeName(typeName) ?? expectedType ?? typeof(AnimationClip);
            if (!XREngine.AnimationClipCookedBinarySerializer.CanHandle(targetType))
            {
                reader.Position = rewind;
                value = null;
                return false;
            }

            value = XREngine.AnimationClipCookedBinarySerializer.Read(reader);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (!allowCustom || value is not AnimationClip animationClip)
                return false;

            calculator.AddBytes(SizeOfTypeName(runtimeType) + XREngine.AnimationClipCookedBinarySerializer.CalculateSize(animationClip));
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (!allowCustom || value is not AnimationClip animationClip)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddExpandedCustomModelValueNode(node, runtimeType, AnimationClipSerialization.CreateModel(animationClip), typeof(AnimationClipSerializedModel), "AnimationClip custom serializer writes AnimationClipSerializedModel via WriteValue");
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!allowCustom || !typeof(AnimationClip).IsAssignableFrom(type))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddExpandedCustomModelSchemaNode(node, type, typeof(AnimationClipSerializedModel), "AnimationClip custom serializer writes AnimationClipSerializedModel via WriteValue");
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}
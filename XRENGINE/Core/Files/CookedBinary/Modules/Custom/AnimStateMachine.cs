using XREngine.Animation;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class AnimStateMachineCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(1500, "AnimStateMachine", "AnimStateMachine custom payload via AnimStateMachineSerializedModel.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (!allowCustom || value is not AnimStateMachine stateMachine)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.CustomObject);
            WriteTypeName(writer, runtimeType);
            XREngine.AnimStateMachineCookedBinarySerializer.Write(writer, stateMachine);
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
            Type targetType = TryResolveSerializedTypeName(typeName) ?? expectedType ?? typeof(AnimStateMachine);
            if (!XREngine.AnimStateMachineCookedBinarySerializer.CanHandle(targetType))
            {
                reader.Position = rewind;
                value = null;
                return false;
            }

            value = XREngine.AnimStateMachineCookedBinarySerializer.Read(reader);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (!allowCustom || value is not AnimStateMachine stateMachine)
                return false;

            calculator.AddBytes(SizeOfTypeName(runtimeType) + XREngine.AnimStateMachineCookedBinarySerializer.CalculateSize(stateMachine));
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (!allowCustom || value is not AnimStateMachine stateMachine)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddExpandedCustomModelValueNode(node, runtimeType, AnimStateMachineSerialization.CreateModel(stateMachine), typeof(AnimStateMachineSerializedModel), "AnimStateMachine custom serializer writes AnimStateMachineSerializedModel via WriteValue");
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!allowCustom || !typeof(AnimStateMachine).IsAssignableFrom(type))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddExpandedCustomModelSchemaNode(node, type, typeof(AnimStateMachineSerializedModel), "AnimStateMachine custom serializer writes AnimStateMachineSerializedModel via WriteValue");
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}
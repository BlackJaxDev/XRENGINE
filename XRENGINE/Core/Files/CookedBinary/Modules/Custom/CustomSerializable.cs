using XREngine.Serialization;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class CustomSerializableCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(1600, "ICookedBinarySerializable", "Opaque custom payloads for explicit cooked-binary implementations.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (!allowCustom || value is not ICookedBinarySerializable custom)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.CustomObject);
            WriteTypeName(writer, runtimeType);
            custom.WriteCookedBinary(writer);
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.CustomObject)
            {
                value = null;
                return false;
            }

            value = ReadCustomObject(reader, expectedType, callbacks);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (!allowCustom || value is not ICookedBinarySerializable custom)
                return false;

            calculator.AddBytes(SizeOfTypeName(runtimeType) + custom.CalculateCookedBinarySize());
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (!allowCustom || value is not ICookedBinarySerializable custom)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddCustomPayloadNode(node, runtimeType, custom.CalculateCookedBinarySize(), $"{nameof(ICookedBinarySerializable)} payload");
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!allowCustom || !typeof(ICookedBinarySerializable).IsAssignableFrom(type))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddCustomSchemaNode(node, type, $"opaque {nameof(ICookedBinarySerializable)} payload");
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}
using XREngine.Serialization;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class CustomSerializableCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(1600, "ICookedBinarySerializable", "Opaque custom payloads for explicit cooked-binary implementations.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (!allowCustom)
                return false;

            if (value is ICookedBinarySerializable custom)
            {
                writer.Write((byte)CookedBinaryTypeMarker.CustomObject);
                WriteTypeName(writer, runtimeType);
                custom.WriteCookedBinary(writer);
                return true;
            }

            if (value is not IRuntimeCookedBinarySerializable)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.CustomObject);
            WriteTypeName(writer, runtimeType);

            byte[] payload = RuntimeCookedBinarySerializer.Serialize(value);
            writer.Write(payload.Length);
            writer.Write(payload);
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
            if (!allowCustom)
                return false;

            if (value is ICookedBinarySerializable custom)
            {
                calculator.AddBytes(SizeOfTypeName(runtimeType) + custom.CalculateCookedBinarySize());
                return true;
            }

            if (value is not IRuntimeCookedBinarySerializable)
                return false;

            calculator.AddBytes(SizeOfTypeName(runtimeType) + sizeof(int) + RuntimeCookedBinarySerializer.CalculateSize(value));
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (!allowCustom)
                return null;

            if (value is ICookedBinarySerializable custom)
            {
                var legacyNode = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
                legacyNode.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
                builder.AddCustomPayloadNode(legacyNode, runtimeType, custom.CalculateCookedBinarySize(), $"{nameof(ICookedBinarySerializable)} payload");
                return builder.FinalizeNode(legacyNode);
            }

            if (value is not IRuntimeCookedBinarySerializable)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            builder.AddCustomPayloadNode(node, runtimeType, sizeof(int) + RuntimeCookedBinarySerializer.CalculateSize(value), $"{nameof(IRuntimeCookedBinarySerializable)} payload");
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!allowCustom)
                return null;

            bool legacyCustom = typeof(ICookedBinarySerializable).IsAssignableFrom(type);
            bool runtimeCustom = typeof(IRuntimeCookedBinarySerializable).IsAssignableFrom(type);
            if (!legacyCustom && !runtimeCustom)
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.CustomObject.ToString();
            string payloadKind = legacyCustom
                ? nameof(ICookedBinarySerializable)
                : nameof(IRuntimeCookedBinarySerializable);
            builder.AddCustomSchemaNode(node, type, $"opaque {payloadKind} payload");
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}

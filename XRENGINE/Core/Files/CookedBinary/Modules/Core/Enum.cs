using System.Globalization;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class EnumCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(800, "Enums", "Enum type names plus Int64 raw payloads.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (!runtimeType.IsEnum)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.Enum);
            WriteTypeName(writer, runtimeType);
            writer.Write(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.Enum)
            {
                value = null;
                return false;
            }

            value = ReadEnum(reader);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (!runtimeType.IsEnum)
                return false;

            calculator.AddBytes(SizeOfTypeName(runtimeType) + sizeof(long));
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (!runtimeType.IsEnum)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.Enum.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringLeaf(node, "enumType", runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name);
            long rawValue = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            builder.AddFixedLeaf(node, "rawValue", "payload", sizeof(long), rawValue.ToString(CultureInfo.InvariantCulture), value.ToString());
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!type.IsEnum)
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.Enum.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringTemplateLeaf(node, "enumType", type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
            builder.AddFixedLeaf(node, "rawValue", "payload", sizeof(long), type.GetEnumUnderlyingType().Name);
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}
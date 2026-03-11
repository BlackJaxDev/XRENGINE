using System.Globalization;
using System.Runtime.InteropServices;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class BlittableStructCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(1700, "BlittableStruct", "Raw byte copies for unmanaged sequential or explicit structs.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (!runtimeType.IsValueType || runtimeType.IsPrimitive || runtimeType.IsEnum || !IsBlittableStruct(runtimeType))
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.BlittableStruct);
            WriteTypeName(writer, runtimeType);
            int size = Marshal.SizeOf(runtimeType);
            writer.Write(size);
            WriteBlittableValue(writer, value, size);
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.BlittableStruct)
            {
                value = null;
                return false;
            }

            value = ReadBlittableStruct(reader);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (!runtimeType.IsValueType || runtimeType.IsPrimitive || runtimeType.IsEnum || !IsBlittableStruct(runtimeType))
                return false;

            calculator.AddBytes(SizeOfTypeName(runtimeType) + sizeof(int) + Marshal.SizeOf(runtimeType));
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (!runtimeType.IsValueType || runtimeType.IsPrimitive || runtimeType.IsEnum || !IsBlittableStruct(runtimeType))
                return null;

            int blittableSize = Marshal.SizeOf(runtimeType);
            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.BlittableStruct.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringLeaf(node, "structType", runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name);
            builder.AddFixedLeaf(node, "size", "length", sizeof(int), blittableSize.ToString(CultureInfo.InvariantCulture));
            builder.AddFixedLeaf(node, "data", "blob", blittableSize, value.ToString(), "blittable raw bytes");
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!type.IsValueType || type.IsPrimitive || type.IsEnum || !IsBlittableStruct(type))
                return null;

            int blittableSize = Marshal.SizeOf(type);
            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.BlittableStruct.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringTemplateLeaf(node, "structType", type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
            builder.AddFixedLeaf(node, "size", "length", sizeof(int), blittableSize.ToString(CultureInfo.InvariantCulture));
            builder.AddFixedLeaf(node, "data", "blob", blittableSize, $"{blittableSize} bytes");
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}
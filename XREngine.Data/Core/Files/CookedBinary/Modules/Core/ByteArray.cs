using System.Globalization;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class ByteArrayCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(200, "ByteArray", "Length-prefixed raw byte arrays.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (value is not byte[] bytes)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.ByteArray);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.ByteArray)
            {
                value = null;
                return false;
            }

            value = ReadByteArray(reader);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (value is not byte[] bytes)
                return false;

            calculator.AddBytes(sizeof(int) + bytes.Length);
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (value is not byte[] bytes)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.ByteArray.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddFixedLeaf(node, "length", "length", sizeof(int), bytes.Length.ToString(CultureInfo.InvariantCulture));
            builder.AddFixedLeaf(node, "data", "blob", bytes.Length, PreviewBytes(bytes), $"{bytes.Length} bytes");
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (type != typeof(byte[]))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.ByteArray.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddFixedLeaf(node, "length", "length", sizeof(int), "variable");
            builder.AddUnknownLeaf(node, "data", "blob", "length bytes");
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}
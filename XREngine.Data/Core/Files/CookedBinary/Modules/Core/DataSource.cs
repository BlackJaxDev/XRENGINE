using System.Globalization;
using XREngine.Data;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class DataSourceCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(300, "DataSource", "Length-prefixed DataSource payloads.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (value is not DataSource dataSource)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.DataSource);
            byte[] blob = dataSource.GetBytes();
            writer.Write(blob.Length);
            writer.Write(blob);
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.DataSource)
            {
                value = null;
                return false;
            }

            value = ReadDataSource(reader);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (value is not DataSource dataSource)
                return false;

            calculator.AddBytes(sizeof(int) + checked((int)dataSource.Length));
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (value is not DataSource dataSource)
                return null;

            byte[] blob = dataSource.GetBytes();
            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.DataSource.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddFixedLeaf(node, "length", "length", sizeof(int), blob.Length.ToString(CultureInfo.InvariantCulture));
            builder.AddFixedLeaf(node, "data", "blob", blob.Length, PreviewBytes(blob), "DataSource payload");
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (type != typeof(DataSource))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.DataSource.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddFixedLeaf(node, "length", "length", sizeof(int), "variable");
            builder.AddUnknownLeaf(node, "data", "blob", "length bytes");
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}
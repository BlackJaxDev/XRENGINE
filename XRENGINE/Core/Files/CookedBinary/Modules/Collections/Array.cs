using System.Globalization;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class ArrayCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(900, "Arrays", "Element-type-prefixed arrays.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (!runtimeType.IsArray || value is not Array array)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.Array);
            Type elementType = runtimeType.GetElementType() ?? typeof(object);
            WriteTypeName(writer, elementType);
            writer.Write(array.Length);
            foreach (object? item in array)
                WriteValue(writer, item, allowCustom, callbacks);
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.Array)
            {
                value = null;
                return false;
            }

            value = ReadArray(reader, callbacks);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (!runtimeType.IsArray || value is not Array array)
                return false;

            Type elementType = runtimeType.GetElementType() ?? typeof(object);
            calculator.AddBytes(SizeOfTypeName(elementType));
            calculator.AddBytes(sizeof(int));
            foreach (object? item in array)
                calculator.AddValue(item, allowCustom);
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (!runtimeType.IsArray || value is not Array array)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.Array.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            Type elementType = runtimeType.GetElementType() ?? typeof(object);
            builder.AddStringLeaf(node, "elementType", elementType.AssemblyQualifiedName ?? elementType.FullName ?? elementType.Name);
            builder.AddFixedLeaf(node, "length", "count", sizeof(int), array.Length.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < array.Length; i++)
                node.MutableChildren.Add(builder.BuildValueNode($"[{i}]", elementType, array.GetValue(i), allowCustom));
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!type.IsArray)
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.Array.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            Type elementType = type.GetElementType() ?? typeof(object);
            builder.AddStringTemplateLeaf(node, "elementType", elementType.AssemblyQualifiedName ?? elementType.FullName ?? elementType.Name);
            builder.AddFixedLeaf(node, "length", "count", sizeof(int), "variable");
            node.MutableChildren.Add(builder.BuildTypeNode("item", elementType, allowCustom));
            node.Notes = "repeated for each array element";
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}
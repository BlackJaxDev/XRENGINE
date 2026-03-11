using System.Collections;
using System.Globalization;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class ListCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(1200, "Lists", "Runtime-type-prefixed IList payloads.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
        {
            if (value is not IList list)
                return false;

            writer.Write((byte)CookedBinaryTypeMarker.List);
            WriteTypeName(writer, runtimeType);
            writer.Write(list.Count);
            foreach (object? item in list)
                WriteValue(writer, item, allowCustom, callbacks);
            return true;
        }

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.List)
            {
                value = null;
                return false;
            }

            value = ReadList(reader, callbacks);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (value is not IList list)
                return false;

            calculator.AddBytes(SizeOfTypeName(runtimeType));
            calculator.AddBytes(sizeof(int));
            foreach (object? item in list)
                calculator.AddValue(item, allowCustom);
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (value is not IList list)
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.List.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringLeaf(node, "listType", runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name);
            builder.AddFixedLeaf(node, "count", "count", sizeof(int), list.Count.ToString(CultureInfo.InvariantCulture));
            Type itemType = TryGetEnumerableItemType(runtimeType) ?? typeof(object);
            for (int i = 0; i < list.Count; i++)
                node.MutableChildren.Add(builder.BuildValueNode($"[{i}]", itemType, list[i], allowCustom));
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!typeof(IList).IsAssignableFrom(type))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.List.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            builder.AddStringTemplateLeaf(node, "listType", type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
            builder.AddFixedLeaf(node, "count", "count", sizeof(int), "variable");
            Type itemType = TryGetEnumerableItemType(type) ?? typeof(object);
            node.MutableChildren.Add(builder.BuildTypeNode("item", itemType, allowCustom));
            node.Notes = "repeated for each list entry";
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}
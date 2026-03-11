using System.Collections;
using System.Globalization;
using System.Linq;

namespace XREngine.Core.Files;

public static partial class CookedBinarySerializer
{
    private sealed class HashSetCookedBinaryModule : CookedBinaryModule
    {
        public override CookedBinarySerializationModuleInfo Info => new(1100, "HashSet", "HashSet<T> element-type-prefixed set entries.");

        public override bool TryWrite(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
            => TryWriteHashSet(writer, value, runtimeType, allowCustom, callbacks);

        public override bool TryRead(CookedBinaryTypeMarker marker, CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks, out object? value)
        {
            if (marker != CookedBinaryTypeMarker.HashSet)
            {
                value = null;
                return false;
            }

            value = ReadHashSet(reader, callbacks);
            return true;
        }

        public override bool TryAddSize(CookedBinarySizeCalculator calculator, object value, Type runtimeType, bool allowCustom)
        {
            if (!runtimeType.IsGenericType || runtimeType.GetGenericTypeDefinition() != typeof(HashSet<>))
                return false;

            Type elementType = runtimeType.GetGenericArguments()[0];
            calculator.AddBytes(SizeOfTypeName(elementType));
            calculator.AddBytes(sizeof(int));
            foreach (object? item in (IEnumerable)value)
                calculator.AddValue(item, allowCustom);
            return true;
        }

        public override CookedBinarySchemaNode? TryBuildValueSchema(CookedBinarySchemaBuilder builder, string name, Type? declaredType, object value, Type runtimeType, bool allowCustom)
        {
            if (!runtimeType.IsGenericType || runtimeType.GetGenericTypeDefinition() != typeof(HashSet<>))
                return null;

            var node = builder.NewNode(name, "value", runtimeType.FullName ?? runtimeType.Name);
            node.Marker = CookedBinaryTypeMarker.HashSet.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            Type elementType = runtimeType.GetGenericArguments()[0];
            builder.AddStringLeaf(node, "elementType", elementType.AssemblyQualifiedName ?? elementType.FullName ?? elementType.Name);
            object?[] items = ((IEnumerable)value).Cast<object?>().ToArray();
            builder.AddFixedLeaf(node, "count", "count", sizeof(int), items.Length.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < items.Length; i++)
                node.MutableChildren.Add(builder.BuildValueNode($"[{i}]", elementType, items[i], allowCustom));
            return builder.FinalizeNode(node);
        }

        public override CookedBinarySchemaNode? TryBuildTypeSchema(CookedBinarySchemaBuilder builder, string name, Type type, bool allowCustom)
        {
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(HashSet<>))
                return null;

            var node = builder.NewNode(name, "schema", type.FullName ?? type.Name);
            node.Marker = CookedBinaryTypeMarker.HashSet.ToString();
            builder.AddFixedLeaf(node, "marker", "marker", 1, node.Marker);
            Type elementType = type.GetGenericArguments()[0];
            builder.AddStringTemplateLeaf(node, "elementType", elementType.AssemblyQualifiedName ?? elementType.FullName ?? elementType.Name);
            builder.AddFixedLeaf(node, "count", "count", sizeof(int), "variable");
            node.MutableChildren.Add(builder.BuildTypeNode("item", elementType, allowCustom));
            node.Notes = "repeated for each set entry";
            return builder.FinalizeNode(node, allowUnknownChildren: true);
        }
    }
}